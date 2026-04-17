using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CLCKTY.App.Core;

public sealed class SoundEngine : ISoundEngine
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int MinImportedSliceMs = 12;
    private const int MaxImportedSliceMs = 1200;
    private const uint MapvkVscToVk = 1;
    private const uint MapvkVscToVkEx = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly WaveFormat OutputFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);

    private readonly object _sync = new();
    private readonly WaveOutEvent _outputDevice;
    private readonly MixingSampleProvider _mixer;
    private readonly Dictionary<string, LoadedSoundProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int virtualKey, KeyEventTrigger trigger), string> _keyMappings = new();
    private readonly HashSet<int> _heldKeys = new();
    private readonly Dictionary<int, CachedSound> _pendingSecondHalf = new();

    private bool _isEnabled = true;
    private float _masterVolume = 0.75f;
    private string _activeProfileId = string.Empty;
    private bool _disposed;

    public SoundEngine()
    {
        _mixer = new MixingSampleProvider(OutputFormat)
        {
            ReadFully = true
        };

        _outputDevice = new WaveOutEvent
        {
            DesiredLatency = 60,
            NumberOfBuffers = 3
        };

        _outputDevice.Init(_mixer);

        LoadBuiltInProfiles();
        _activeProfileId = _profiles.Keys.First();

        _outputDevice.Play();
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _isEnabled;
            }
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _isEnabled = value;
            }
        }
    }

    public float MasterVolume
    {
        get
        {
            lock (_sync)
            {
                return _masterVolume;
            }
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _masterVolume = Math.Clamp(value, 0f, 1f);
            }
        }
    }

    public string ActiveProfileId
    {
        get
        {
            lock (_sync)
            {
                return _activeProfileId;
            }
        }
    }

    public IReadOnlyList<SoundProfileDescriptor> Profiles
    {
        get
        {
            lock (_sync)
            {
                return _profiles.Values
                    .Select(ToDescriptor)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<SoundClipDescriptor> GetClipOptions()
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_profiles.TryGetValue(_activeProfileId, out var profile))
            {
                return Array.Empty<SoundClipDescriptor>();
            }

            return profile.Clips.Values
                .Select(clip => new SoundClipDescriptor(
                    clip.Id,
                    clip.DisplayName,
                    clip.SourceLabel,
                    string.Equals(clip.SourceLabel, "Uploaded", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(clip.Id, profile.DefaultClipId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    public IReadOnlyList<InputMappingDescriptor> GetMappings()
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            return _keyMappings
                .Select(mapping => new InputMappingDescriptor(mapping.Key.virtualKey, mapping.Key.trigger, mapping.Value))
                .ToList();
        }
    }

    public void SetActiveProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            if (_profiles.ContainsKey(profileId))
            {
                _activeProfileId = profileId;
            }
        }
    }

    public void SetKeyMapping(int virtualKey, string? clipId, KeyEventTrigger trigger = KeyEventTrigger.Down)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            var key = (virtualKey, trigger);

            if (string.IsNullOrWhiteSpace(clipId))
            {
                _keyMappings.Remove(key);
                return;
            }

            _keyMappings[key] = clipId;
        }
    }

    public string? GetKeyMapping(int virtualKey, KeyEventTrigger trigger = KeyEventTrigger.Down)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _keyMappings.TryGetValue((virtualKey, trigger), out var clipId) ? clipId : null;
        }
    }

    public bool RemoveClipFromActiveProfile(string clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId))
        {
            return false;
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_profiles.TryGetValue(_activeProfileId, out var activeProfile)
                || !activeProfile.Clips.ContainsKey(clipId)
                || string.Equals(activeProfile.DefaultClipId, clipId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var updatedClips = activeProfile.Clips
                .Where(entry => !string.Equals(entry.Key, clipId, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

            var updatedDefaultKeyClips = activeProfile.DefaultKeyClips
                .Where(entry => !string.Equals(entry.Value, clipId, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => entry.Key, entry => entry.Value);

            var removedMappings = _keyMappings
                .Where(entry => string.Equals(entry.Value, clipId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToList();

            foreach (var mappingKey in removedMappings)
            {
                _keyMappings.Remove(mappingKey);
            }

            _profiles[activeProfile.Id] = new LoadedSoundProfile(
                activeProfile.Id,
                activeProfile.DisplayName,
                activeProfile.DefaultClipId,
                updatedClips,
                updatedDefaultKeyClips,
                activeProfile.IsImported);

            return true;
        }
    }

    public bool RemoveImportedProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_profiles.TryGetValue(profileId, out var profile) || !profile.IsImported)
            {
                return false;
            }

            if (_profiles.Count <= 1)
            {
                return false;
            }

            _profiles.Remove(profileId);

            if (string.Equals(_activeProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfileId = _profiles.Values
                    .OrderBy(candidate => candidate.IsImported)
                    .ThenBy(candidate => candidate.DisplayName)
                    .Select(candidate => candidate.Id)
                    .FirstOrDefault() ?? string.Empty;
            }

            return true;
        }
    }

    public void ClearMappings()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _keyMappings.Clear();
        }
    }

    public void PlayForKey(int virtualKey)
    {
        LoadedSoundProfile? profile;
        SoundClip? clip;
        float volume;

        lock (_sync)
        {
            if (_disposed || !_isEnabled || !_profiles.TryGetValue(_activeProfileId, out profile) || profile is null)
            {
                return;
            }

            var clipId = ResolveClipForKey(profile, virtualKey, KeyEventTrigger.Down);
            if (!profile.Clips.TryGetValue(clipId, out clip))
            {
                clip = profile.Clips[profile.DefaultClipId];
            }

            volume = _masterVolume;
        }

        var provider = new VolumeSampleProvider(new CachedSoundSampleProvider(clip.Sound))
        {
            Volume = volume
        };

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _mixer.AddMixerInput(provider);
        }
    }

    public void StartHoldForKey(int virtualKey)
    {
        LoadedSoundProfile? profile;
        SoundClip? clipToPlay = null;
        float volume = 0f;
        var splitClipOnRelease = false;

        lock (_sync)
        {
            if (_disposed || !_isEnabled || !_profiles.TryGetValue(_activeProfileId, out profile) || profile is null)
            {
                return;
            }

            if (_heldKeys.Contains(virtualKey))
            {
                return;
            }

            _heldKeys.Add(virtualKey);

            SoundClip? mappedDownClip = null;
            var hasMappedDownClip =
                _keyMappings.TryGetValue((virtualKey, KeyEventTrigger.Down), out var downClipId)
                && profile.Clips.TryGetValue(downClipId, out mappedDownClip);

            var hasMappedUpClip =
                _keyMappings.TryGetValue((virtualKey, KeyEventTrigger.Up), out var upClipId)
                && profile.Clips.ContainsKey(upClipId);

            if (hasMappedDownClip)
            {
                clipToPlay = mappedDownClip;
                _pendingSecondHalf.Remove(virtualKey);
            }
            else if (hasMappedUpClip)
            {
                // Up-only custom mappings should only trigger on release.
                _pendingSecondHalf.Remove(virtualKey);
                return;
            }
            else
            {
                var clipId = ResolveClipForKey(profile, virtualKey, KeyEventTrigger.Down);
                if (!profile.Clips.TryGetValue(clipId, out clipToPlay))
                {
                    clipToPlay = profile.Clips[profile.DefaultClipId];
                }

                splitClipOnRelease = true;
            }

            volume = _masterVolume;
        }

        if (clipToPlay is null)
        {
            return;
        }

        if (!splitClipOnRelease)
        {
            var mappedProvider = new VolumeSampleProvider(new CachedSoundSampleProvider(clipToPlay.Sound))
            {
                Volume = volume
            };

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _mixer.AddMixerInput(mappedProvider);
            }

            return;
        }

        // Play first half immediately and stash second half for release
        try
        {
            var audio = clipToPlay.Sound.AudioData;
            var total = audio.Length;
            var half = Math.Max(1, total / 2);

            var firstProvider = new VolumeSampleProvider(new PartialCachedSoundSampleProvider(clipToPlay.Sound, 0, half))
            {
                Volume = volume
            };

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                // prepare and store second half
                if (half < total)
                {
                    var remaining = new float[total - half];
                    Array.Copy(audio, half, remaining, 0, remaining.Length);
                    _pendingSecondHalf[virtualKey] = new CachedSound(clipToPlay.Sound.WaveFormat, remaining);
                }

                _mixer.AddMixerInput(firstProvider);
            }
        }
        catch (Exception)
        {
            // fall back to playing whole clip on error
            PlayForKey(virtualKey);
        }
    }

    public void ReleaseForKey(int virtualKey)
    {
        SoundClip? mappedUpClip = null;
        CachedSound? second = null;
        float volume;

        lock (_sync)
        {
            if (!_heldKeys.Remove(virtualKey))
            {
                return;
            }

            if (_profiles.TryGetValue(_activeProfileId, out var profile)
                && _keyMappings.TryGetValue((virtualKey, KeyEventTrigger.Up), out var upClipId)
                && profile.Clips.TryGetValue(upClipId, out var resolvedUpClip))
            {
                mappedUpClip = resolvedUpClip;
            }

            if (mappedUpClip is null && _pendingSecondHalf.TryGetValue(virtualKey, out var cached))
            {
                second = cached;
            }

            _pendingSecondHalf.Remove(virtualKey);
            volume = _masterVolume;
        }

        try
        {
            if (mappedUpClip is not null)
            {
                var providerMapped = new VolumeSampleProvider(new CachedSoundSampleProvider(mappedUpClip.Sound))
                {
                    Volume = volume
                };

                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _mixer.AddMixerInput(providerMapped);
                }
                return;
            }

            if (second is null)
            {
                return;
            }

            var provider = new VolumeSampleProvider(new CachedSoundSampleProvider(second))
            {
                Volume = volume
            };

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _mixer.AddMixerInput(provider);
            }
        }
        catch (Exception)
        {
            // ignore
        }
    }

    public async Task<string?> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        var imported = await Task.Run(() => LoadProfileFromFolder(folderPath, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (imported is null)
        {
            return null;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            _profiles[imported.Id] = imported;
        }

        return imported.Id;
    }

    public async Task<string?> ImportAudioClipAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var importedClip = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var clipIdSeed = NormalizeToken(fileName);
            var clipDisplayName = ToDisplayName(fileName);
            var clipSound = LoadCachedSoundFromFile(filePath);

            return new SoundClip(clipIdSeed, clipDisplayName, clipSound, "Uploaded");
        }, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_profiles.TryGetValue(_activeProfileId, out var activeProfile))
            {
                return null;
            }

            var newClips = activeProfile.Clips.Values
                .ToDictionary(clip => clip.Id, clip => clip, StringComparer.OrdinalIgnoreCase);

            var uniqueClipId = EnsureUniqueClipId(newClips.Keys, importedClip.Id);
            var updatedClip = new SoundClip(uniqueClipId, importedClip.DisplayName, importedClip.Sound, importedClip.SourceLabel);
            newClips[uniqueClipId] = updatedClip;

            var updatedProfile = new LoadedSoundProfile(
                activeProfile.Id,
                activeProfile.DisplayName,
                activeProfile.DefaultClipId,
                newClips,
                activeProfile.DefaultKeyClips,
                activeProfile.IsImported);

            _profiles[updatedProfile.Id] = updatedProfile;
            return uniqueClipId;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _outputDevice.Stop();
            _outputDevice.Dispose();
            _disposed = true;
        }
    }

    private static SoundProfileDescriptor ToDescriptor(LoadedSoundProfile profile)
    {
        var clips = profile.Clips.Values
            .Select(clip => new SoundClipDescriptor(clip.Id, clip.DisplayName, clip.SourceLabel, false))
            .ToList();

        return new SoundProfileDescriptor(profile.Id, profile.DisplayName, clips, profile.IsImported);
    }

    private string ResolveClipForKey(LoadedSoundProfile profile, int virtualKey, KeyEventTrigger trigger)
    {
        if (_keyMappings.TryGetValue((virtualKey, trigger), out var mappedClipId) && profile.Clips.ContainsKey(mappedClipId))
        {
            return mappedClipId;
        }

        if (trigger == KeyEventTrigger.Down && profile.DefaultKeyClips.TryGetValue(virtualKey, out var profileClipId) && profile.Clips.ContainsKey(profileClipId))
        {
            return profileClipId;
        }

        return profile.DefaultClipId;
    }

    private void LoadBuiltInProfiles()
    {
        var neonClips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new SoundClip("default", "Default", CreateSynthClick(1920, 32, 0.11f, 0.70f, 1.97)),
            ["accent"] = new SoundClip("accent", "Accent", CreateSynthClick(2240, 24, 0.06f, 0.62f, 2.18)),
            ["heavy"] = new SoundClip("heavy", "Heavy", CreateSynthClick(1580, 40, 0.18f, 0.82f, 1.73))
        };

        var stealthClips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new SoundClip("default", "Default", CreateSynthClick(1460, 34, 0.08f, 0.60f, 2.04)),
            ["accent"] = new SoundClip("accent", "Accent", CreateSynthClick(1840, 26, 0.05f, 0.55f, 2.22)),
            ["heavy"] = new SoundClip("heavy", "Heavy", CreateSynthClick(1180, 44, 0.16f, 0.78f, 1.65))
        };

        var crystalClips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new SoundClip("default", "Default", CreateSynthClick(2080, 30, 0.10f, 0.68f, 2.40)),
            ["accent"] = new SoundClip("accent", "Accent", CreateSynthClick(2620, 20, 0.03f, 0.50f, 2.62)),
            ["heavy"] = new SoundClip("heavy", "Heavy", CreateSynthClick(1700, 40, 0.14f, 0.74f, 1.88))
        };

        _profiles["neon-blue"] = new LoadedSoundProfile(
            "neon-blue",
            "Neon Blue",
            "default",
            neonClips,
            CreateDefaultKeyMap(),
            false);

        _profiles["stealth-black"] = new LoadedSoundProfile(
            "stealth-black",
            "Stealth Black",
            "default",
            stealthClips,
            CreateDefaultKeyMap(),
            false);

        _profiles["crystal-violet"] = new LoadedSoundProfile(
            "crystal-violet",
            "Crystal Violet",
            "default",
            crystalClips,
            CreateDefaultKeyMap(),
            false);
    }

    private static IReadOnlyDictionary<int, string> CreateDefaultKeyMap()
    {
        return new Dictionary<int, string>
        {
            [0x20] = "accent",
            [0x0D] = "heavy",
            [0x08] = "heavy"
        };
    }

    private static CachedSound CreateSynthClick(
        double baseFrequency,
        int durationMs,
        float noiseMix,
        float gain,
        double harmonic)
    {
        var sampleCount = Math.Max(1, OutputSampleRate * durationMs / 1000);
        var audioData = new float[sampleCount * OutputChannels];

        var random = new Random(HashCode.Combine(baseFrequency, durationMs, noiseMix, gain));
        var phaseA = 0.0;
        var phaseB = 0.0;
        var incrementA = 2.0 * Math.PI * baseFrequency / OutputSampleRate;
        var incrementB = 2.0 * Math.PI * baseFrequency * harmonic / OutputSampleRate;

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleCount;
            var envelope = (float)(Math.Exp(-10.8 * t) * Math.Min(1.0, t * 58.0));
            var tonal = (float)((Math.Sin(phaseA) * 0.72) + (Math.Sin(phaseB) * 0.28));
            var noise = (float)((random.NextDouble() * 2.0) - 1.0);
            var sample = (tonal * (1f - noiseMix) + noise * noiseMix) * envelope * gain;

            var index = i * 2;
            audioData[index] = sample;
            audioData[index + 1] = sample;

            phaseA += incrementA;
            phaseB += incrementB;
        }

        return new CachedSound(OutputFormat, audioData);
    }

    private static LoadedSoundProfile? LoadProfileFromFolder(string folderPath, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(folderPath, "config.json");
        if (File.Exists(configPath))
        {
            var configProfile = LoadProfileFromConfig(folderPath, configPath, cancellationToken);
            if (configProfile is not null)
            {
                return configProfile;
            }
        }

        var waveFiles = Directory.EnumerateFiles(folderPath, "*.wav", SearchOption.TopDirectoryOnly)
            .ToList();

        if (waveFiles.Count == 0)
        {
            return null;
        }

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var packLabel = ToDisplayName(folderName);

        var clips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase);

        foreach (var waveFile in waveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(waveFile);
            var clipId = NormalizeToken(name);
            var displayName = ToDisplayName(name);
            var clipSound = LoadCachedSoundFromFile(waveFile);

            clips[clipId] = new SoundClip(clipId, displayName, clipSound, packLabel);
        }

        if (clips.Count == 0)
        {
            return null;
        }

        var defaultClipId = clips.ContainsKey("default") ? "default" : clips.Keys.First();
        var baseId = NormalizeToken(folderName);
        var uniqueId = Guid.NewGuid().ToString("N")[..6];
        var profileId = $"custom-{baseId}-{uniqueId}";

        return new LoadedSoundProfile(
            profileId,
            $"{packLabel} (Custom)",
            defaultClipId,
            clips,
            CreateDefaultKeyMap(),
            true);
    }

    private static LoadedSoundProfile? LoadProfileFromConfig(string folderPath, string configPath, CancellationToken cancellationToken)
    {
        ImportedSoundPackConfig? config;

        try
        {
            var json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize<ImportedSoundPackConfig>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }

        if (config is null || string.IsNullOrWhiteSpace(config.Sound) || config.Defines is null || config.Defines.Count == 0)
        {
            return null;
        }

        var soundFilePath = Path.Combine(folderPath, config.Sound);
        if (!File.Exists(soundFilePath))
        {
            return null;
        }

        float[] sourceSamples;

        try
        {
            sourceSamples = PrepareImportedSamples(LoadNormalizedSamplesFromFile(soundFilePath));
        }
        catch (Exception)
        {
            return null;
        }

        if (sourceSamples.Length < OutputChannels)
        {
            return null;
        }

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var displayName = string.IsNullOrWhiteSpace(config.Name) ? ToDisplayName(folderName) : config.Name.Trim();

        var clips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase);
        var keyMap = new Dictionary<int, string>();
        var sliceToClip = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var define in config.Defines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasImportedKey = int.TryParse(define.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var importedKeyCode);

            var slice = define.Value;
            if (slice is null || slice.Length == 0)
            {
                continue;
            }

            var startMs = Math.Max(0, slice[0]);
            var durationMs = slice.Length > 1
                ? Math.Clamp(slice[1], MinImportedSliceMs, MaxImportedSliceMs)
                : 180;
            var sliceKey = FormattableString.Invariant($"{startMs}:{durationMs}");

            if (!sliceToClip.TryGetValue(sliceKey, out var clipId))
            {
                var sliced = SliceCachedSound(sourceSamples, startMs, durationMs);
                if (sliced is null)
                {
                    continue;
                }

                clipId = $"seg{clips.Count + 1:D3}";
                var segmentName = $"Segment {clips.Count + 1}";
                clips[clipId] = new SoundClip(clipId, segmentName, sliced, displayName);
                sliceToClip[sliceKey] = clipId;
            }

            if (hasImportedKey && TryMapImportedKeyCodeToVirtualKey(importedKeyCode, out var virtualKey))
            {
                keyMap[virtualKey] = clipId;
            }
        }

        if (clips.Count == 0)
        {
            return null;
        }

        var normalizedIdSeed = string.IsNullOrWhiteSpace(config.Id) ? displayName : config.Id;
        var baseId = NormalizeToken(normalizedIdSeed);
        var uniqueId = Guid.NewGuid().ToString("N")[..6];
        var profileId = $"custom-{baseId}-{uniqueId}";
        var defaultClipId = clips.Keys.First();

        return new LoadedSoundProfile(
            profileId,
            $"{displayName} (Imported)",
            defaultClipId,
            clips,
            keyMap.Count > 0 ? keyMap : new Dictionary<int, string>(),
            true);
    }

    private static CachedSound LoadCachedSoundFromFile(string filePath)
    {
        var sampleData = PrepareImportedSamples(LoadNormalizedSamplesFromFile(filePath));
        return new CachedSound(OutputFormat, sampleData);
    }

    private static float[] PrepareImportedSamples(float[] sourceSamples)
    {
        if (sourceSamples.Length == 0)
        {
            return sourceSamples;
        }

        var peak = 0f;

        for (var i = 0; i < sourceSamples.Length; i++)
        {
            var abs = Math.Abs(sourceSamples[i]);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        if (peak <= 0f)
        {
            return sourceSamples;
        }

        const float targetPeak = 0.92f;
        var gain = peak > targetPeak ? targetPeak / peak : 1f;
        var prepared = new float[sourceSamples.Length];

        for (var i = 0; i < sourceSamples.Length; i++)
        {
            prepared[i] = Math.Clamp(sourceSamples[i] * gain, -1f, 1f);
        }

        var fadeFrames = Math.Min(96, Math.Max(2, (prepared.Length / OutputChannels) / 40));
        ApplyFade(prepared, fadeFrames);
        return prepared;
    }

    private static CachedSound? SliceCachedSound(float[] sourceSamples, int startMs, int durationMs)
    {
        if (sourceSamples.Length < OutputChannels)
        {
            return null;
        }

        var totalFrames = sourceSamples.Length / OutputChannels;
        var startFrame = (int)Math.Round((startMs / 1000d) * OutputSampleRate, MidpointRounding.AwayFromZero);
        if (startFrame >= totalFrames)
        {
            return null;
        }

        var requestedFrames = (int)Math.Round((durationMs / 1000d) * OutputSampleRate, MidpointRounding.AwayFromZero);
        requestedFrames = Math.Max(OutputSampleRate * MinImportedSliceMs / 1000, requestedFrames);

        var availableFrames = totalFrames - startFrame;
        var frameCount = Math.Min(requestedFrames, availableFrames);
        if (frameCount < 8)
        {
            return null;
        }

        var segment = new float[frameCount * OutputChannels];
        Array.Copy(sourceSamples, startFrame * OutputChannels, segment, 0, segment.Length);
        ApplyFade(segment, Math.Min(96, frameCount / 4));
        return new CachedSound(OutputFormat, segment);
    }

    private static bool TryMapImportedKeyCodeToVirtualKey(int importedKeyCode, out int virtualKey)
    {
        virtualKey = 0;
        if (importedKeyCode <= 0)
        {
            return false;
        }

        var mapped = MapVirtualKey(unchecked((uint)importedKeyCode), MapvkVscToVkEx);

        if (mapped == 0 && importedKeyCode > 0xFF)
        {
            var lowByteScanCode = (uint)(importedKeyCode & 0xFF);
            mapped = MapVirtualKey(lowByteScanCode, MapvkVscToVkEx);

            if (mapped == 0)
            {
                mapped = MapVirtualKey(lowByteScanCode, MapvkVscToVk);
            }
        }

        if (mapped == 0 && importedKeyCode <= 0xFF)
        {
            mapped = (uint)importedKeyCode;
        }

        if (mapped == 0 || mapped > 0xFE)
        {
            return false;
        }

        virtualKey = (int)mapped;
        return true;
    }

    private static float[] LoadNormalizedSamplesFromFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        if (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".oga", StringComparison.OrdinalIgnoreCase))
        {
            return ReadVorbisSamples(filePath);
        }

        try
        {
            using var reader = new AudioFileReader(filePath);
            return ReadNormalizedSamples(reader);
        }
        catch (Exception)
        {
            using var mediaReader = new MediaFoundationReader(filePath);
            return ReadNormalizedSamples(mediaReader.ToSampleProvider());
        }
    }

    private static float[] ReadVorbisSamples(string filePath)
    {
        using var vorbisReader = new VorbisReader(filePath);

        var sourceChannels = Math.Max(1, vorbisReader.Channels);
        var sourceSampleRate = Math.Max(1, vorbisReader.SampleRate);
        var readBufferSize = Math.Max(4096 * sourceChannels, sourceSampleRate * sourceChannels / 6);
        var readBuffer = ArrayPool<float>.Shared.Rent(readBufferSize);

        try
        {
            var rawSamples = new List<float>(sourceSampleRate * sourceChannels);
            int read;

            while ((read = vorbisReader.ReadSamples(readBuffer, 0, readBuffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    rawSamples.Add(readBuffer[i]);
                }
            }

            if (rawSamples.Count == 0)
            {
                return Array.Empty<float>();
            }

            return ConvertRawInterleavedToOutputStereo(rawSamples.ToArray(), sourceChannels, sourceSampleRate);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuffer);
        }
    }

    private static float[] ConvertRawInterleavedToOutputStereo(float[] rawSamples, int sourceChannels, int sourceSampleRate)
    {
        var sourceFrames = rawSamples.Length / sourceChannels;
        if (sourceFrames <= 0)
        {
            return Array.Empty<float>();
        }

        var stereoSamples = new float[sourceFrames * OutputChannels];

        if (sourceChannels == 1)
        {
            for (var frame = 0; frame < sourceFrames; frame++)
            {
                var sample = rawSamples[frame];
                var offset = frame * OutputChannels;
                stereoSamples[offset] = sample;
                stereoSamples[offset + 1] = sample;
            }
        }
        else
        {
            for (var frame = 0; frame < sourceFrames; frame++)
            {
                var sourceOffset = frame * sourceChannels;
                var targetOffset = frame * OutputChannels;
                stereoSamples[targetOffset] = rawSamples[sourceOffset];
                stereoSamples[targetOffset + 1] = rawSamples[sourceOffset + 1];
            }
        }

        if (sourceSampleRate == OutputSampleRate)
        {
            return stereoSamples;
        }

        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (OutputSampleRate / (double)sourceSampleRate), MidpointRounding.AwayFromZero));
        var resampled = new float[targetFrames * OutputChannels];

        for (var frame = 0; frame < targetFrames; frame++)
        {
            var sourcePosition = frame * (sourceSampleRate / (double)OutputSampleRate);
            var sourceFrame = (int)sourcePosition;
            var nextFrame = Math.Min(sourceFrame + 1, sourceFrames - 1);
            var fraction = (float)(sourcePosition - sourceFrame);

            var sourceOffset = sourceFrame * OutputChannels;
            var nextOffset = nextFrame * OutputChannels;
            var targetOffset = frame * OutputChannels;

            var left = stereoSamples[sourceOffset] + ((stereoSamples[nextOffset] - stereoSamples[sourceOffset]) * fraction);
            var right = stereoSamples[sourceOffset + 1] + ((stereoSamples[nextOffset + 1] - stereoSamples[sourceOffset + 1]) * fraction);

            resampled[targetOffset] = left;
            resampled[targetOffset + 1] = right;
        }

        return resampled;
    }

    private static float[] ReadNormalizedSamples(ISampleProvider provider)
    {
        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            var multiplexer = new MultiplexingSampleProvider(new[] { provider }, OutputChannels);
            multiplexer.ConnectInputToOutput(0, 0);
            multiplexer.ConnectInputToOutput(1, 1);
            provider = multiplexer;
        }
        else if (provider.WaveFormat.Channels != OutputChannels)
        {
            throw new NotSupportedException($"Unsupported channel count for import ({provider.WaveFormat.Channels}).");
        }

        if (provider.WaveFormat.SampleRate != OutputSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);
        }

        return ReadAllSamples(provider);
    }

    private static float[] ReadAllSamples(ISampleProvider sampleProvider)
    {
        var readBuffer = ArrayPool<float>.Shared.Rent(OutputSampleRate * OutputChannels / 2);

        try
        {
            var sampleList = new List<float>(OutputSampleRate * OutputChannels / 6);
            int read;

            while ((read = sampleProvider.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    sampleList.Add(readBuffer[i]);
                }
            }

            return sampleList.ToArray();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuffer);
        }
    }

    private static string NormalizeToken(string value)
    {
        var token = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        return string.IsNullOrWhiteSpace(token) ? "clip" : token;
    }

    private static string EnsureUniqueClipId(IEnumerable<string> existingClipIds, string preferredId)
    {
        var used = new HashSet<string>(existingClipIds, StringComparer.OrdinalIgnoreCase);
        var sanitized = NormalizeToken(preferredId);

        if (!used.Contains(sanitized))
        {
            return sanitized;
        }

        var suffix = 2;

        while (true)
        {
            var candidate = $"{sanitized}{suffix}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static string ToDisplayName(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "Unnamed";
        }

        var normalized = token.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }

    private static void ApplyFade(float[] samples, int fadeFrames)
    {
        if (samples.Length < OutputChannels * 3)
        {
            return;
        }

        var totalFrames = samples.Length / OutputChannels;
        var clampedFadeFrames = Math.Clamp(fadeFrames, 1, Math.Max(1, totalFrames / 3));

        for (var frame = 0; frame < clampedFadeFrames; frame++)
        {
            var gain = frame / (float)clampedFadeFrames;
            var offset = frame * OutputChannels;

            for (var channel = 0; channel < OutputChannels; channel++)
            {
                samples[offset + channel] *= gain;
            }
        }

        for (var frame = 0; frame < clampedFadeFrames; frame++)
        {
            var gain = 1f - (frame / (float)clampedFadeFrames);
            var offset = (totalFrames - 1 - frame) * OutputChannels;

            for (var channel = 0; channel < OutputChannels; channel++)
            {
                samples[offset + channel] *= gain;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private sealed class LoadedSoundProfile
    {
        public LoadedSoundProfile(
            string id,
            string displayName,
            string defaultClipId,
            IReadOnlyDictionary<string, SoundClip> clips,
            IReadOnlyDictionary<int, string> defaultKeyClips,
            bool isImported)
        {
            Id = id;
            DisplayName = displayName;
            DefaultClipId = defaultClipId;
            Clips = clips;
            DefaultKeyClips = defaultKeyClips;
            IsImported = isImported;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string DefaultClipId { get; }

        public IReadOnlyDictionary<string, SoundClip> Clips { get; }

        public IReadOnlyDictionary<int, string> DefaultKeyClips { get; }

        public bool IsImported { get; }
    }

    private sealed class SoundClip
    {
        public SoundClip(string id, string displayName, CachedSound sound, string sourceLabel = "Stock")
        {
            Id = id;
            DisplayName = displayName;
            Sound = sound;
            SourceLabel = sourceLabel;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public CachedSound Sound { get; }

        public string SourceLabel { get; }
    }

    private sealed class ImportedSoundPackConfig
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("sound")]
        public string? Sound { get; init; }

        [JsonPropertyName("key_define_type")]
        public string? KeyDefineType { get; init; }

        [JsonPropertyName("defines")]
        public Dictionary<string, int[]>? Defines { get; init; }
    }
}
