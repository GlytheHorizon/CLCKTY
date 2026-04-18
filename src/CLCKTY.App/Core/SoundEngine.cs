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
    private const string MechvibesPreviewCredit = "From MechVibesDX (Preview)";
    private const string ImportedCredit = "Imported";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly WaveFormat OutputFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);

    private readonly object _sync = new();
    private WaveOutEvent _outputDevice;
    private readonly MixingSampleProvider _mixer;
    private readonly Dictionary<string, LoadedSoundProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int virtualKey, KeyEventTrigger trigger), string> _keyMappings = new();
    private readonly HashSet<int> _heldKeys = new();
    private readonly Dictionary<int, PendingClipSegment> _pendingSecondHalf = new();
    private readonly HashSet<int> _suppressAutoReleaseForMappedDown = new();

    private bool _isEnabled = true;
    private float _masterVolume = 0.75f;
    private string _activeKeyboardProfileId = string.Empty;
    private string _activeMouseProfileId = string.Empty;
    private int _outputDeviceNumber = -1;
    private bool _disposed;

    public SoundEngine()
    {
        _mixer = new MixingSampleProvider(OutputFormat)
        {
            ReadFully = true
        };

        _outputDevice = CreateOutputDevice(_outputDeviceNumber);

        _outputDevice.Init(_mixer);

        LoadBuiltInProfiles();
        _activeKeyboardProfileId = FindFirstProfileId(false);
        _activeMouseProfileId = FindFirstProfileId(true);

        if (string.IsNullOrWhiteSpace(_activeMouseProfileId))
        {
            _activeMouseProfileId = _activeKeyboardProfileId;
        }

        if (string.IsNullOrWhiteSpace(_activeKeyboardProfileId))
        {
            _activeKeyboardProfileId = _activeMouseProfileId;
        }

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
            return GetActiveProfileId(false);
        }
    }

    public IReadOnlyList<SoundProfileDescriptor> Profiles
    {
        get
        {
            return GetProfiles(false);
        }
    }

    public string GetActiveProfileId(bool isMouseProfile)
    {
        lock (_sync)
        {
            return isMouseProfile ? _activeMouseProfileId : _activeKeyboardProfileId;
        }
    }

    public IReadOnlyList<SoundProfileDescriptor> GetProfiles(bool isMouseProfile)
    {
        lock (_sync)
        {
            return _profiles.Values
                .Where(profile => profile.IsMouseProfile == isMouseProfile)
                .Select(ToDescriptor)
                .ToList();
        }
    }

    public IReadOnlyList<SoundClipDescriptor> GetClipOptions()
    {
        return GetClipOptions(false);
    }

    public IReadOnlyList<SoundClipDescriptor> GetClipOptions(bool isMouseProfile)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            var activeProfileId = isMouseProfile ? _activeMouseProfileId : _activeKeyboardProfileId;
            if (!_profiles.TryGetValue(activeProfileId, out var profile))
            {
                return Array.Empty<SoundClipDescriptor>();
            }

            return profile.Clips.Values
                .Select(clip => new SoundClipDescriptor(
                    clip.Id,
                    clip.DisplayName,
                    BuildClipOptionSourceLabel(profile, clip),
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
        SetActiveProfile(profileId, false);
    }

    public void SetActiveProfile(string profileId, bool isMouseProfile)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            if (_profiles.TryGetValue(profileId, out var profile)
                && profile.IsMouseProfile == isMouseProfile)
            {
                if (isMouseProfile)
                {
                    _activeMouseProfileId = profileId;
                }
                else
                {
                    _activeKeyboardProfileId = profileId;
                }
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
        return RemoveClipFromActiveProfile(clipId, false);
    }

    public bool RemoveClipFromActiveProfile(string clipId, bool isMouseProfile)
    {
        if (string.IsNullOrWhiteSpace(clipId))
        {
            return false;
        }

        lock (_sync)
        {
            ThrowIfDisposed();

            var activeProfileId = isMouseProfile ? _activeMouseProfileId : _activeKeyboardProfileId;

            if (!_profiles.TryGetValue(activeProfileId, out var activeProfile)
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

            var updatedDefaultKeyUpClips = activeProfile.DefaultKeyUpClips
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
                updatedDefaultKeyUpClips,
                activeProfile.IsImported,
                activeProfile.IsMouseProfile,
                activeProfile.SourceLabel);

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

            var categoryCount = _profiles.Values.Count(candidate => candidate.IsMouseProfile == profile.IsMouseProfile);
            if (categoryCount <= 1)
            {
                return false;
            }

            _profiles.Remove(profileId);

            if (profile.IsMouseProfile && string.Equals(_activeMouseProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                _activeMouseProfileId = _profiles.Values
                    .Where(candidate => candidate.IsMouseProfile)
                    .OrderBy(candidate => candidate.IsImported)
                    .ThenBy(candidate => candidate.DisplayName)
                    .Select(candidate => candidate.Id)
                    .FirstOrDefault() ?? string.Empty;
            }

            if (!profile.IsMouseProfile && string.Equals(_activeKeyboardProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                _activeKeyboardProfileId = _profiles.Values
                    .Where(candidate => !candidate.IsMouseProfile)
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

    public void PlayForKey(int virtualKey, float categoryVolume = 1f)
    {
        LoadedSoundProfile? profile;
        SoundClip? clip;
        float volume;
        var isMouseInput = InputBindingCode.IsMouseCode(virtualKey);

        lock (_sync)
        {
            var activeProfileId = isMouseInput ? _activeMouseProfileId : _activeKeyboardProfileId;
            if (_disposed || !_isEnabled || !_profiles.TryGetValue(activeProfileId, out profile) || profile is null)
            {
                return;
            }

            var clipId = ResolveClipForKey(profile, virtualKey, KeyEventTrigger.Down);
            if (!profile.Clips.TryGetValue(clipId, out clip))
            {
                clip = profile.Clips[profile.DefaultClipId];
            }

            volume = Math.Clamp(_masterVolume * categoryVolume, 0f, 1f);
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

    public void StartHoldForKey(int virtualKey, float categoryVolume = 1f)
    {
        LoadedSoundProfile? profile;
        SoundClip? clipToPlay = null;
        float volume = 0f;
        var splitClipOnRelease = false;
        var isMouseInput = InputBindingCode.IsMouseCode(virtualKey);

        lock (_sync)
        {
            var activeProfileId = isMouseInput ? _activeMouseProfileId : _activeKeyboardProfileId;
            if (_disposed || !_isEnabled || !_profiles.TryGetValue(activeProfileId, out profile) || profile is null)
            {
                return;
            }

            if (_heldKeys.Contains(virtualKey))
            {
                return;
            }

            _heldKeys.Add(virtualKey);
            var isOneShotCustomProfile = IsOneShotCustomProfile(profile);

            var hasMappedDownClip = TryResolveMappedClip(virtualKey, KeyEventTrigger.Down, profile, out var mappedDownClip);

            var hasMappedUpClip = !isOneShotCustomProfile
                && TryResolveMappedClip(virtualKey, KeyEventTrigger.Up, profile, out _);

            var hasProfileUpClip = !isOneShotCustomProfile
                && profile.DefaultKeyUpClips.TryGetValue(virtualKey, out var profileUpClipId)
                && profile.Clips.ContainsKey(profileUpClipId);

            if (hasMappedDownClip)
            {
                clipToPlay = mappedDownClip;
                _pendingSecondHalf.Remove(virtualKey);

                if (hasMappedUpClip)
                {
                    _suppressAutoReleaseForMappedDown.Remove(virtualKey);
                }
                else
                {
                    _suppressAutoReleaseForMappedDown.Add(virtualKey);
                }
            }
            else if (hasMappedUpClip)
            {
                // Up-only custom mappings should only trigger on release.
                _suppressAutoReleaseForMappedDown.Remove(virtualKey);
                _pendingSecondHalf.Remove(virtualKey);
                return;
            }
            else
            {
                _suppressAutoReleaseForMappedDown.Remove(virtualKey);

                var clipId = ResolveClipForKey(profile, virtualKey, KeyEventTrigger.Down);
                if (!profile.Clips.TryGetValue(clipId, out clipToPlay))
                {
                    clipToPlay = profile.Clips[profile.DefaultClipId];
                }

                splitClipOnRelease = !isOneShotCustomProfile && !hasMappedUpClip && !hasProfileUpClip;
            }

            volume = Math.Clamp(_masterVolume * categoryVolume, 0f, 1f);
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

                // Keep only segment offsets for release; avoid per-key array allocations.
                if (half < total)
                {
                    _pendingSecondHalf[virtualKey] = new PendingClipSegment(
                        clipToPlay.Sound,
                        half,
                        total - half);
                }

                _mixer.AddMixerInput(firstProvider);
            }
        }
        catch (Exception)
        {
            // fall back to playing whole clip on error
            PlayForKey(virtualKey, categoryVolume);
        }
    }

    public void ReleaseForKey(int virtualKey, float categoryVolume = 1f)
    {
        SoundClip? mappedUpClip = null;
        PendingClipSegment? second = null;
        var suppressAutoRelease = false;
        float volume;
        var isMouseInput = InputBindingCode.IsMouseCode(virtualKey);

        lock (_sync)
        {
            if (!_heldKeys.Remove(virtualKey))
            {
                return;
            }

            var activeProfileId = isMouseInput ? _activeMouseProfileId : _activeKeyboardProfileId;

            if (_profiles.TryGetValue(activeProfileId, out var oneShotProfile)
                && IsOneShotCustomProfile(oneShotProfile))
            {
                _suppressAutoReleaseForMappedDown.Remove(virtualKey);
                _pendingSecondHalf.Remove(virtualKey);
                return;
            }

            suppressAutoRelease = _suppressAutoReleaseForMappedDown.Remove(virtualKey);

            if (_profiles.TryGetValue(activeProfileId, out var profile)
                && TryResolveMappedClip(virtualKey, KeyEventTrigger.Up, profile, out var resolvedUpClip)
                && resolvedUpClip is not null)
            {
                mappedUpClip = resolvedUpClip;
            }

            if (!suppressAutoRelease
                && mappedUpClip is null
                && _profiles.TryGetValue(activeProfileId, out var activeProfile)
                && activeProfile.DefaultKeyUpClips.TryGetValue(virtualKey, out var defaultUpClipId)
                && activeProfile.Clips.TryGetValue(defaultUpClipId, out var resolvedDefaultUpClip))
            {
                mappedUpClip = resolvedDefaultUpClip;
            }

            if (!suppressAutoRelease
                && mappedUpClip is null
                && _pendingSecondHalf.TryGetValue(virtualKey, out var cached))
            {
                second = cached;
            }

            _pendingSecondHalf.Remove(virtualKey);
            volume = Math.Clamp(_masterVolume * categoryVolume, 0f, 1f);
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

            var pendingSegment = second.Value;

            var provider = new VolumeSampleProvider(new PartialCachedSoundSampleProvider(
                pendingSegment.Sound,
                pendingSegment.StartIndex,
                pendingSegment.Length))
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

    private static bool IsOneShotCustomProfile(LoadedSoundProfile profile)
    {
        return profile.IsImported
            && (string.Equals(profile.SourceLabel, "custom", StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile.SourceLabel, "custome", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolveMappedClip(
        int inputCode,
        KeyEventTrigger trigger,
        LoadedSoundProfile activeProfile,
        out SoundClip? resolvedClip)
    {
        resolvedClip = null;

        if (!_keyMappings.TryGetValue((inputCode, trigger), out var clipId)
            || string.IsNullOrWhiteSpace(clipId))
        {
            return false;
        }

        if (activeProfile.Clips.TryGetValue(clipId, out var clipInActiveProfile))
        {
            resolvedClip = clipInActiveProfile;
            return true;
        }

        foreach (var profile in _profiles.Values)
        {
            if (profile.Clips.TryGetValue(clipId, out var clipInAnyProfile))
            {
                resolvedClip = clipInAnyProfile;
                return true;
            }
        }

        return false;
    }

    public Task<string?> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        return ImportSoundPackAsync(folderPath, false, ImportedCredit, cancellationToken);
    }

    public async Task<string?> ImportSoundPackAsync(string folderPath, bool isMouseProfile, CancellationToken cancellationToken = default)
    {
        return await ImportSoundPackAsync(folderPath, isMouseProfile, ImportedCredit, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> ImportSoundPackAsync(string folderPath, bool isMouseProfile, string sourceLabel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        var resolvedSourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? ImportedCredit : sourceLabel.Trim();

        var imported = await Task.Run(() => LoadProfileFromFolder(folderPath, cancellationToken, isMouseProfile, true, resolvedSourceLabel), cancellationToken)
            .ConfigureAwait(false);

        if (imported is null)
        {
            var configCandidates = Directory.EnumerateFiles(folderPath, "config.json", SearchOption.AllDirectories)
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(40)
                .Cast<string>()
                .ToList();

            foreach (var candidate in configCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                imported = LoadProfileFromFolder(candidate, cancellationToken, isMouseProfile, true, resolvedSourceLabel);
                if (imported is not null)
                {
                    break;
                }
            }
        }

        if (imported is null)
        {
            return null;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            _profiles[imported.Id] = imported;

            if (imported.IsMouseProfile)
            {
                _activeMouseProfileId = imported.Id;
            }
            else
            {
                _activeKeyboardProfileId = imported.Id;
            }
        }

        return imported.Id;
    }

    public Task<string?> ImportAudioClipAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return ImportAudioClipAsync(filePath, false, cancellationToken);
    }

    public async Task<string?> ImportAudioClipAsync(string filePath, bool isMouseProfile, CancellationToken cancellationToken = default)
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

            var activeProfileId = isMouseProfile ? _activeMouseProfileId : _activeKeyboardProfileId;
            if (!_profiles.TryGetValue(activeProfileId, out var activeProfile))
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
                activeProfile.DefaultKeyUpClips,
                activeProfile.IsImported,
                activeProfile.IsMouseProfile,
                activeProfile.SourceLabel);

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

    public IReadOnlyList<string> GetOutputDevices()
    {
        var devices = new List<string> { "System Default" };

        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var capabilities = WaveOut.GetCapabilities(i);
            devices.Add(capabilities.ProductName);
        }

        return devices;
    }

    public int GetOutputDeviceNumber()
    {
        lock (_sync)
        {
            return _outputDeviceNumber;
        }
    }

    public bool SetOutputDeviceNumber(int deviceNumber)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (deviceNumber < -1 || deviceNumber >= WaveOut.DeviceCount)
            {
                return false;
            }

            if (_outputDeviceNumber == deviceNumber)
            {
                return true;
            }

            var replacement = CreateOutputDevice(deviceNumber);

            try
            {
                replacement.Init(_mixer);
                replacement.Play();

                _outputDevice.Stop();
                _outputDevice.Dispose();

                _outputDevice = replacement;
                _outputDeviceNumber = deviceNumber;
                return true;
            }
            catch
            {
                replacement.Dispose();
                return false;
            }
        }
    }

    private static SoundProfileDescriptor ToDescriptor(LoadedSoundProfile profile)
    {
        var clips = profile.Clips.Values
            .Select(clip => new SoundClipDescriptor(clip.Id, clip.DisplayName, clip.SourceLabel, false))
            .ToList();

        return new SoundProfileDescriptor(
            profile.Id,
            profile.DisplayName,
            clips,
            profile.IsImported,
            profile.IsMouseProfile,
            profile.SourceLabel);
    }

    public string GetPlaybackClipDisplayName(int inputCode, KeyEventTrigger trigger = KeyEventTrigger.Down)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            var activeProfileId = InputBindingCode.IsMouseCode(inputCode)
                ? _activeMouseProfileId
                : _activeKeyboardProfileId;

            if (!_profiles.TryGetValue(activeProfileId, out var profile))
            {
                return "Default";
            }

            var clipId = ResolveClipForKey(profile, inputCode, trigger);
            if (profile.Clips.TryGetValue(clipId, out var clip))
            {
                return BuildPlaybackDisplayName(profile, clip);
            }

            if (profile.Clips.TryGetValue(profile.DefaultClipId, out var defaultClip))
            {
                return BuildPlaybackDisplayName(profile, defaultClip);
            }

            return "Default";
        }
    }

    private static string BuildPlaybackDisplayName(LoadedSoundProfile profile, SoundClip clip)
    {
        if (string.Equals(clip.SourceLabel, "Uploaded", StringComparison.OrdinalIgnoreCase))
        {
            return $"Uploaded: {clip.DisplayName}";
        }

        if (clip.Id.StartsWith("seg", StringComparison.OrdinalIgnoreCase)
            || clip.DisplayName.StartsWith("Segment ", StringComparison.OrdinalIgnoreCase))
        {
            return $"{profile.DisplayName} ({clip.SourceLabel})";
        }

        if (!string.Equals(clip.SourceLabel, "Stock", StringComparison.OrdinalIgnoreCase))
        {
            return $"{clip.DisplayName} ({clip.SourceLabel})";
        }

        return clip.DisplayName;
    }

    private static string BuildClipOptionSourceLabel(LoadedSoundProfile profile, SoundClip clip)
    {
        if (string.IsNullOrWhiteSpace(clip.SourceLabel))
        {
            return string.Empty;
        }

        if (string.Equals(clip.SourceLabel, "Stock", StringComparison.OrdinalIgnoreCase))
        {
            return clip.SourceLabel;
        }

        var packName = NormalizePackDisplayName(profile.DisplayName);
        if (string.IsNullOrWhiteSpace(packName))
        {
            return clip.SourceLabel;
        }

        return $"{packName} - {clip.SourceLabel}";
    }

    private static string NormalizePackDisplayName(string profileDisplayName)
    {
        if (string.IsNullOrWhiteSpace(profileDisplayName))
        {
            return string.Empty;
        }

        return profileDisplayName
            .Replace(" (Imported)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" (Custom)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
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

        if (trigger == KeyEventTrigger.Up && profile.DefaultKeyUpClips.TryGetValue(virtualKey, out var profileUpClipId) && profile.Clips.ContainsKey(profileUpClipId))
        {
            return profileUpClipId;
        }

        return profile.DefaultClipId;
    }

    private string FindFirstProfileId(bool isMouseProfile)
    {
        return _profiles.Values
            .Where(profile => profile.IsMouseProfile == isMouseProfile)
            .OrderBy(profile => profile.IsImported)
            .ThenBy(profile => profile.DisplayName)
            .Select(profile => profile.Id)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string CreateProfileId(string normalizedSeed, bool isMouseProfile, bool isImported)
    {
        var category = isMouseProfile ? "mouse" : "keyboard";

        if (!isImported)
        {
            return $"stock-{category}-{normalizedSeed}";
        }

        var uniqueId = Guid.NewGuid().ToString("N")[..6];
        return $"custom-{category}-{normalizedSeed}-{uniqueId}";
    }

    private void LoadBuiltInProfiles()
    {
        _profiles.Clear();

        foreach (var bundledProfile in LoadBundledProfilesFromAssets())
        {
            _profiles[bundledProfile.Id] = bundledProfile;
        }

        if (!_profiles.Values.Any(profile => !profile.IsMouseProfile))
        {
            AddFallbackKeyboardProfiles();
        }

        if (!_profiles.Values.Any(profile => profile.IsMouseProfile))
        {
            AddFallbackMouseProfile();
        }
    }

    private static IEnumerable<LoadedSoundProfile> LoadBundledProfilesFromAssets()
    {
        var bundledRoot = ResolveBundledSoundsRoot();
        if (string.IsNullOrWhiteSpace(bundledRoot) || !Directory.Exists(bundledRoot))
        {
            yield break;
        }

        foreach (var keyboardProfile in LoadBundledProfilesFromCategory(Path.Combine(bundledRoot, "keyboard"), false))
        {
            yield return keyboardProfile;
        }

        foreach (var mouseProfile in LoadBundledProfilesFromCategory(Path.Combine(bundledRoot, "mouse"), true))
        {
            yield return mouseProfile;
        }
    }

    private static IEnumerable<LoadedSoundProfile> LoadBundledProfilesFromCategory(string categoryFolder, bool isMouseProfile)
    {
        if (!Directory.Exists(categoryFolder))
        {
            yield break;
        }

        foreach (var profileFolder in Directory.EnumerateDirectories(categoryFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var sourceLabel = ResolveBundledProfileSourceLabel(profileFolder, isMouseProfile);

            var loaded = LoadProfileFromFolder(
                profileFolder,
                CancellationToken.None,
                isMouseProfile,
                false,
                sourceLabel);

            if (loaded is not null)
            {
                yield return loaded;
            }
        }
    }

    private static string ResolveBundledProfileSourceLabel(string profileFolderPath, bool isMouseProfile)
    {
        if (isMouseProfile)
        {
            return MechvibesPreviewCredit;
        }

        var folderName = Path.GetFileName(profileFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(folderName, "Razer Green (Blackwidow Elite) - Akira", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "tealios-v2_Akira", StringComparison.OrdinalIgnoreCase))
        {
            return "by Akira";
        }

        if (string.Equals(folderName, "Model F XT", StringComparison.OrdinalIgnoreCase))
        {
            return "by Unknown";
        }

        return MechvibesPreviewCredit;
    }

    private static string ResolveBundledSoundsRoot()
    {
        var outputAssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
        if (Directory.Exists(outputAssetsPath))
        {
            return outputAssetsPath;
        }

        var localAssetsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "Sounds"));
        return localAssetsPath;
    }

    private void AddFallbackKeyboardProfiles()
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
            new Dictionary<int, string>(),
            false,
            false,
            "Stock");

        _profiles["stealth-black"] = new LoadedSoundProfile(
            "stealth-black",
            "Stealth Black",
            "default",
            stealthClips,
            CreateDefaultKeyMap(),
            new Dictionary<int, string>(),
            false,
            false,
            "Stock");

        _profiles["crystal-violet"] = new LoadedSoundProfile(
            "crystal-violet",
            "Crystal Violet",
            "default",
            crystalClips,
            CreateDefaultKeyMap(),
            new Dictionary<int, string>(),
            false,
            false,
            "Stock");
    }

    private void AddFallbackMouseProfile()
    {
        var mouseClips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new SoundClip("default", "Mouse Click", CreateSynthClick(1260, 30, 0.16f, 0.76f, 1.59)),
            ["accent"] = new SoundClip("accent", "Mouse Tick", CreateSynthClick(1580, 24, 0.10f, 0.70f, 2.05)),
            ["heavy"] = new SoundClip("heavy", "Mouse Thock", CreateSynthClick(980, 42, 0.21f, 0.84f, 1.31))
        };

        var mouseMap = new Dictionary<int, string>
        {
            [InputBindingCode.MouseLeft] = "default",
            [InputBindingCode.MouseRight] = "accent",
            [InputBindingCode.MouseMiddle] = "heavy",
            [InputBindingCode.MouseX1] = "accent",
            [InputBindingCode.MouseX2] = "heavy"
        };

        _profiles["fallback-mouse"] = new LoadedSoundProfile(
            "fallback-mouse",
            "Mouse Clicks",
            "default",
            mouseClips,
            mouseMap,
            new Dictionary<int, string>(),
            false,
            true,
            "Stock");
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

    private static IReadOnlyDictionary<int, string> BuildRandomizedKeyMap(IReadOnlyList<string> clipIds, bool isMouseProfile)
    {
        if (clipIds.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var random = new Random();
        var map = new Dictionary<int, string>();

        if (isMouseProfile)
        {
            var mouseKeys = new[]
            {
                InputBindingCode.MouseLeft,
                InputBindingCode.MouseRight,
                InputBindingCode.MouseMiddle,
                InputBindingCode.MouseX1,
                InputBindingCode.MouseX2
            };

            foreach (var mouseKey in mouseKeys)
            {
                map[mouseKey] = clipIds[random.Next(clipIds.Count)];
            }

            return map;
        }

        for (var virtualKey = 0x08; virtualKey <= 0xFE; virtualKey++)
        {
            map[virtualKey] = clipIds[random.Next(clipIds.Count)];
        }

        return map;
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

    private static LoadedSoundProfile? LoadProfileFromFolder(
        string folderPath,
        CancellationToken cancellationToken,
        bool isMouseProfile,
        bool isImported,
        string sourceLabel)
    {
        var configPath = Path.Combine(folderPath, "config.json");
        if (File.Exists(configPath))
        {
            var configProfile = LoadProfileFromConfig(folderPath, configPath, cancellationToken, isMouseProfile, isImported, sourceLabel);
            if (configProfile is not null)
            {
                return configProfile;
            }
        }

        var audioFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var ext = Path.GetExtension(path);
                return ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (audioFiles.Count == 0)
        {
            foreach (var candidateFolder in EnumerateCandidatePackFolders(folderPath, cancellationToken))
            {
                var nested = LoadProfileFromFolder(candidateFolder, cancellationToken, isMouseProfile, isImported, sourceLabel);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var packLabel = ToDisplayName(folderName);

        var clips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase);

        foreach (var audioFile in audioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(audioFile);
            var clipId = NormalizeToken(name);
            var displayName = ToDisplayName(name);
            var clipSound = LoadCachedSoundFromFile(audioFile);

            clips[clipId] = new SoundClip(clipId, displayName, clipSound, sourceLabel);
        }

        if (clips.Count == 0)
        {
            return null;
        }

        var defaultClipId = clips.ContainsKey("default") ? "default" : clips.Keys.First();
        var baseId = NormalizeToken(folderName);
        var profileId = CreateProfileId(baseId, isMouseProfile, isImported);
        var profileDisplayName = packLabel;
        var randomizedKeyMap = BuildRandomizedKeyMap(clips.Keys.ToList(), isMouseProfile);

        return new LoadedSoundProfile(
            profileId,
            profileDisplayName,
            defaultClipId,
            clips,
            randomizedKeyMap,
            new Dictionary<int, string>(),
            isImported,
            isMouseProfile,
            sourceLabel);
    }

    private static IEnumerable<string> EnumerateCandidatePackFolders(string rootFolder, CancellationToken cancellationToken)
    {
        IEnumerable<string> firstLevel;

        try
        {
            firstLevel = Directory.EnumerateDirectories(rootFolder, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var folder in firstLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return folder;

            IEnumerable<string> secondLevel;
            try
            {
                secondLevel = Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var nested in secondLevel)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return nested;
            }
        }
    }

    private static LoadedSoundProfile? LoadProfileFromConfig(
        string folderPath,
        string configPath,
        CancellationToken cancellationToken,
        bool isMouseProfile,
        bool isImported,
        string sourceLabel)
    {
        ImportedSoundPackConfig? config;
        string json;

        try
        {
            json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize<ImportedSoundPackConfig>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }

        if (config is null || string.IsNullOrWhiteSpace(config.Sound) || config.Defines is null || config.Defines.Count == 0)
        {
            return LoadProfileFromMechvibesConfig(folderPath, json, cancellationToken, isMouseProfile, isImported, sourceLabel);
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
                clips[clipId] = new SoundClip(clipId, segmentName, sliced, sourceLabel);
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
        var profileId = CreateProfileId(baseId, isMouseProfile, isImported);
        var profileDisplayName = isImported ? $"{displayName} (Imported)" : displayName;
        var defaultClipId = clips.Keys.First();

        return new LoadedSoundProfile(
            profileId,
            profileDisplayName,
            defaultClipId,
            clips,
            keyMap.Count > 0 ? keyMap : new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            isImported,
            isMouseProfile,
            sourceLabel);
    }

    private static LoadedSoundProfile? LoadProfileFromMechvibesConfig(
        string folderPath,
        string rawJson,
        CancellationToken cancellationToken,
        bool isMouseProfile,
        bool isImported,
        string sourceLabel)
    {
        MechvibesSoundPackConfig? config;

        try
        {
            config = JsonSerializer.Deserialize<MechvibesSoundPackConfig>(rawJson, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }

        if (config is null
            || string.IsNullOrWhiteSpace(config.AudioFile)
            || config.Definitions is null
            || config.Definitions.Count == 0)
        {
            return null;
        }

        var soundFilePath = Path.Combine(folderPath, config.AudioFile);
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
        var keyDownMap = new Dictionary<int, string>();
        var keyUpMap = new Dictionary<int, string>();
        var sliceToClip = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var definition in config.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryMapImportedKeyNameToInputCode(definition.Key, out var mappedInputCode))
            {
                continue;
            }

            var timingPairs = definition.Value?.Timing;
            if (timingPairs is null || timingPairs.Count == 0)
            {
                continue;
            }

            if (TryGetOrCreateClipFromTiming(
                sourceSamples,
                sourceLabel,
                timingPairs,
                0,
                clips,
                sliceToClip,
                out var downClipId))
            {
                keyDownMap[mappedInputCode] = downClipId;
            }

            if (TryGetOrCreateClipFromTiming(
                sourceSamples,
                sourceLabel,
                timingPairs,
                1,
                clips,
                sliceToClip,
                out var upClipId))
            {
                keyUpMap[mappedInputCode] = upClipId;
            }
        }

        if (clips.Count == 0)
        {
            return null;
        }

        var normalizedIdSeed = string.IsNullOrWhiteSpace(config.Id) ? displayName : config.Id;
        var baseId = NormalizeToken(normalizedIdSeed);
        var profileId = CreateProfileId(baseId, isMouseProfile, isImported);
        var profileDisplayName = isImported ? $"{displayName} (Imported)" : displayName;
        var defaultClipId = clips.Keys.First();

        return new LoadedSoundProfile(
            profileId,
            profileDisplayName,
            defaultClipId,
            clips,
            keyDownMap.Count > 0 ? keyDownMap : new Dictionary<int, string>(),
            keyUpMap.Count > 0 ? keyUpMap : new Dictionary<int, string>(),
            isImported,
            isMouseProfile,
            sourceLabel);
    }

    private static bool TryGetOrCreateClipFromTiming(
        float[] sourceSamples,
        string sourceLabel,
        IReadOnlyList<float[]> timingPairs,
        int timingIndex,
        IDictionary<string, SoundClip> clips,
        IDictionary<string, string> sliceToClip,
        out string clipId)
    {
        clipId = string.Empty;

        if (timingIndex < 0 || timingIndex >= timingPairs.Count)
        {
            return false;
        }

        var timing = timingPairs[timingIndex];
        if (timing.Length < 2)
        {
            return false;
        }

        var rawStart = Math.Min(timing[0], timing[1]);
        var rawEnd = Math.Max(timing[0], timing[1]);

        var startMs = Math.Max(0, (int)Math.Round(rawStart, MidpointRounding.AwayFromZero));
        var durationMs = Math.Clamp(
            (int)Math.Round(rawEnd - rawStart, MidpointRounding.AwayFromZero),
            MinImportedSliceMs,
            MaxImportedSliceMs);

        var sliceKey = FormattableString.Invariant($"{startMs}:{durationMs}");
        if (sliceToClip.TryGetValue(sliceKey, out var existingClipId))
        {
            clipId = existingClipId ?? string.Empty;
            return !string.IsNullOrEmpty(clipId);
        }

        var sliced = SliceCachedSound(sourceSamples, startMs, durationMs);
        if (sliced is null)
        {
            clipId = string.Empty;
            return false;
        }

        clipId = $"seg{clips.Count + 1:D3}";
        var clipDisplayName = $"Segment {clips.Count + 1}";
        clips[clipId] = new SoundClip(clipId, clipDisplayName, sliced, sourceLabel);
        sliceToClip[sliceKey] = clipId;

        return true;
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

    private static bool TryMapImportedKeyNameToInputCode(string? keyName, out int inputCode)
    {
        inputCode = 0;

        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        if (string.Equals(keyName, "MouseLeft", StringComparison.OrdinalIgnoreCase))
        {
            inputCode = InputBindingCode.MouseLeft;
            return true;
        }

        if (string.Equals(keyName, "MouseRight", StringComparison.OrdinalIgnoreCase))
        {
            inputCode = InputBindingCode.MouseRight;
            return true;
        }

        if (string.Equals(keyName, "MouseMiddle", StringComparison.OrdinalIgnoreCase))
        {
            inputCode = InputBindingCode.MouseMiddle;
            return true;
        }

        if (string.Equals(keyName, "MouseX1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(keyName, "MouseBack", StringComparison.OrdinalIgnoreCase))
        {
            inputCode = InputBindingCode.MouseX1;
            return true;
        }

        if (string.Equals(keyName, "MouseX2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(keyName, "MouseForward", StringComparison.OrdinalIgnoreCase))
        {
            inputCode = InputBindingCode.MouseX2;
            return true;
        }

        if (keyName.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && keyName.Length == 4)
        {
            var letter = char.ToUpperInvariant(keyName[3]);
            if (letter >= 'A' && letter <= 'Z')
            {
                inputCode = letter;
                return true;
            }
        }

        if (keyName.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && keyName.Length == 6)
        {
            var digit = keyName[5];
            if (digit >= '0' && digit <= '9')
            {
                inputCode = digit;
                return true;
            }
        }

        if (keyName.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = keyName[6..];

            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numpadDigit)
                && numpadDigit >= 0
                && numpadDigit <= 9)
            {
                inputCode = 0x60 + numpadDigit;
                return true;
            }

            switch (suffix.ToLowerInvariant())
            {
                case "add":
                    inputCode = 0x6B;
                    return true;
                case "subtract":
                    inputCode = 0x6D;
                    return true;
                case "multiply":
                    inputCode = 0x6A;
                    return true;
                case "divide":
                    inputCode = 0x6F;
                    return true;
                case "decimal":
                    inputCode = 0x6E;
                    return true;
                case "enter":
                    inputCode = 0x0D;
                    return true;
            }
        }

        if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(keyName[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var functionKey)
            && functionKey >= 1
            && functionKey <= 24)
        {
            inputCode = 0x70 + (functionKey - 1);
            return true;
        }

        switch (keyName)
        {
            case "Escape":
                inputCode = 0x1B;
                return true;
            case "Tab":
                inputCode = 0x09;
                return true;
            case "CapsLock":
                inputCode = 0x14;
                return true;
            case "ShiftLeft":
                inputCode = 0xA0;
                return true;
            case "ShiftRight":
                inputCode = 0xA1;
                return true;
            case "ControlLeft":
                inputCode = 0xA2;
                return true;
            case "ControlRight":
                inputCode = 0xA3;
                return true;
            case "AltLeft":
                inputCode = 0xA4;
                return true;
            case "AltRight":
                inputCode = 0xA5;
                return true;
            case "MetaLeft":
                inputCode = 0x5B;
                return true;
            case "MetaRight":
                inputCode = 0x5C;
                return true;
            case "ContextMenu":
                inputCode = 0x5D;
                return true;
            case "Space":
                inputCode = 0x20;
                return true;
            case "Enter":
                inputCode = 0x0D;
                return true;
            case "Backspace":
                inputCode = 0x08;
                return true;
            case "Backquote":
                inputCode = 0xC0;
                return true;
            case "Minus":
                inputCode = 0xBD;
                return true;
            case "Equal":
                inputCode = 0xBB;
                return true;
            case "BracketLeft":
                inputCode = 0xDB;
                return true;
            case "BracketRight":
                inputCode = 0xDD;
                return true;
            case "Backslash":
                inputCode = 0xDC;
                return true;
            case "Semicolon":
                inputCode = 0xBA;
                return true;
            case "Quote":
                inputCode = 0xDE;
                return true;
            case "Comma":
                inputCode = 0xBC;
                return true;
            case "Period":
                inputCode = 0xBE;
                return true;
            case "Slash":
                inputCode = 0xBF;
                return true;
            case "Insert":
                inputCode = 0x2D;
                return true;
            case "Delete":
                inputCode = 0x2E;
                return true;
            case "Home":
                inputCode = 0x24;
                return true;
            case "End":
                inputCode = 0x23;
                return true;
            case "PageUp":
                inputCode = 0x21;
                return true;
            case "PageDown":
                inputCode = 0x22;
                return true;
            case "ArrowUp":
                inputCode = 0x26;
                return true;
            case "ArrowDown":
                inputCode = 0x28;
                return true;
            case "ArrowLeft":
                inputCode = 0x25;
                return true;
            case "ArrowRight":
                inputCode = 0x27;
                return true;
            case "PrintScreen":
                inputCode = 0x2C;
                return true;
            case "ScrollLock":
                inputCode = 0x91;
                return true;
            case "Pause":
                inputCode = 0x13;
                return true;
            case "NumLock":
                inputCode = 0x90;
                return true;
            default:
                return false;
        }
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

    private static WaveOutEvent CreateOutputDevice(int deviceNumber)
    {
        return new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            // Slightly higher buffering avoids crackle on some drivers while staying responsive.
            DesiredLatency = 60,
            NumberOfBuffers = 3
        };
    }

    private readonly struct PendingClipSegment
    {
        public PendingClipSegment(CachedSound sound, int startIndex, int length)
        {
            Sound = sound;
            StartIndex = startIndex;
            Length = length;
        }

        public CachedSound Sound { get; }

        public int StartIndex { get; }

        public int Length { get; }
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
            IReadOnlyDictionary<int, string> defaultKeyUpClips,
            bool isImported,
            bool isMouseProfile,
            string sourceLabel)
        {
            Id = id;
            DisplayName = displayName;
            DefaultClipId = defaultClipId;
            Clips = clips;
            DefaultKeyClips = defaultKeyClips;
            DefaultKeyUpClips = defaultKeyUpClips;
            IsImported = isImported;
            IsMouseProfile = isMouseProfile;
            SourceLabel = sourceLabel;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string DefaultClipId { get; }

        public IReadOnlyDictionary<string, SoundClip> Clips { get; }

        public IReadOnlyDictionary<int, string> DefaultKeyClips { get; }

        public IReadOnlyDictionary<int, string> DefaultKeyUpClips { get; }

        public bool IsImported { get; }

        public bool IsMouseProfile { get; }

        public string SourceLabel { get; }
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

    private sealed class MechvibesSoundPackConfig
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("audio_file")]
        public string? AudioFile { get; init; }

        [JsonPropertyName("definition_method")]
        public string? DefinitionMethod { get; init; }

        [JsonPropertyName("definitions")]
        public Dictionary<string, MechvibesKeyDefinition>? Definitions { get; init; }
    }

    private sealed class MechvibesKeyDefinition
    {
        [JsonPropertyName("timing")]
        public List<float[]>? Timing { get; init; }
    }
}
