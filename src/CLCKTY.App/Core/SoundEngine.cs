using System.Buffers;
using System.Globalization;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CLCKTY.App.Core;

public sealed class SoundEngine : ISoundEngine
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;

    private static readonly WaveFormat OutputFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);

    private readonly object _sync = new();
    private readonly WaveOutEvent _outputDevice;
    private readonly MixingSampleProvider _mixer;
    private readonly Dictionary<string, LoadedSoundProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _keyMappings = new();

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
            DesiredLatency = 35,
            NumberOfBuffers = 2
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
                .Select(clip => new SoundClipDescriptor(clip.Id, clip.DisplayName))
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

    public void SetKeyMapping(int virtualKey, string? clipId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(clipId))
            {
                _keyMappings.Remove(virtualKey);
                return;
            }

            _keyMappings[virtualKey] = clipId;
        }
    }

    public string? GetKeyMapping(int virtualKey)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _keyMappings.TryGetValue(virtualKey, out var clipId) ? clipId : null;
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

            var clipId = ResolveClipForKey(profile, virtualKey);
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
            .Select(clip => new SoundClipDescriptor(clip.Id, clip.DisplayName))
            .ToList();

        return new SoundProfileDescriptor(profile.Id, profile.DisplayName, clips);
    }

    private string ResolveClipForKey(LoadedSoundProfile profile, int virtualKey)
    {
        if (_keyMappings.TryGetValue(virtualKey, out var mappedClipId) && profile.Clips.ContainsKey(mappedClipId))
        {
            return mappedClipId;
        }

        if (profile.DefaultKeyClips.TryGetValue(virtualKey, out var profileClipId) && profile.Clips.ContainsKey(profileClipId))
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
            CreateDefaultKeyMap());

        _profiles["stealth-black"] = new LoadedSoundProfile(
            "stealth-black",
            "Stealth Black",
            "default",
            stealthClips,
            CreateDefaultKeyMap());

        _profiles["crystal-violet"] = new LoadedSoundProfile(
            "crystal-violet",
            "Crystal Violet",
            "default",
            crystalClips,
            CreateDefaultKeyMap());
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
        var waveFiles = Directory.EnumerateFiles(folderPath, "*.wav", SearchOption.TopDirectoryOnly)
            .ToList();

        if (waveFiles.Count == 0)
        {
            return null;
        }

        var clips = new Dictionary<string, SoundClip>(StringComparer.OrdinalIgnoreCase);

        foreach (var waveFile in waveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileNameWithoutExtension(waveFile);
            var clipId = NormalizeToken(name);
            var displayName = ToDisplayName(name);
            var clipSound = LoadCachedSoundFromFile(waveFile);

            clips[clipId] = new SoundClip(clipId, displayName, clipSound);
        }

        if (clips.Count == 0)
        {
            return null;
        }

        var defaultClipId = clips.ContainsKey("default") ? "default" : clips.Keys.First();
        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var baseId = NormalizeToken(folderName);
        var uniqueId = Guid.NewGuid().ToString("N")[..6];
        var profileId = $"custom-{baseId}-{uniqueId}";

        return new LoadedSoundProfile(
            profileId,
            $"{ToDisplayName(folderName)} (Custom)",
            defaultClipId,
            clips,
            CreateDefaultKeyMap());
    }

    private static CachedSound LoadCachedSoundFromFile(string filePath)
    {
        using var reader = new AudioFileReader(filePath);

        ISampleProvider provider = reader;

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels != 2)
        {
            throw new NotSupportedException($"Unsupported channel count for {Path.GetFileName(filePath)}.");
        }

        if (provider.WaveFormat.SampleRate != OutputSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);
        }

        var sampleData = ReadAllSamples(provider);
        return new CachedSound(OutputFormat, sampleData);
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

    private static string ToDisplayName(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "Unnamed";
        }

        var normalized = token.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class LoadedSoundProfile
    {
        public LoadedSoundProfile(
            string id,
            string displayName,
            string defaultClipId,
            IReadOnlyDictionary<string, SoundClip> clips,
            IReadOnlyDictionary<int, string> defaultKeyClips)
        {
            Id = id;
            DisplayName = displayName;
            DefaultClipId = defaultClipId;
            Clips = clips;
            DefaultKeyClips = defaultKeyClips;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string DefaultClipId { get; }

        public IReadOnlyDictionary<string, SoundClip> Clips { get; }

        public IReadOnlyDictionary<int, string> DefaultKeyClips { get; }
    }

    private sealed class SoundClip
    {
        public SoundClip(string id, string displayName, CachedSound sound)
        {
            Id = id;
            DisplayName = displayName;
            Sound = sound;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public CachedSound Sound { get; }
    }
}
