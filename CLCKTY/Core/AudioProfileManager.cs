using NAudio.Wave;
using System.IO;

namespace CLCKTY.Core;

public sealed class AudioProfileManager : IAudioProfileManager
{
    private static readonly string[] SupportedExtensions = [".wav", ".mp3", ".aiff", ".wma"];

    private readonly string _stockRoot;
    private readonly string _importedRoot;
    private readonly string _recordedRoot;

    public AudioProfileManager()
    {
        var outputRoot = AppContext.BaseDirectory;
        _stockRoot = Path.Combine(outputRoot, "Assets", "Sounds", "Stock");

        var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CLCKTY", "Sounds");
        _importedRoot = Path.Combine(appDataRoot, "Imported");
        _recordedRoot = Path.Combine(appDataRoot, "Recorded");
    }

    public async Task<IReadOnlyList<AudioProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAssetRootsAsync(cancellationToken).ConfigureAwait(false);

        var stockAssets = BuildAssetsFromFolder("stock", _stockRoot, SoundBadge.Stock);
        var stockAssignments = BuildAssignments(stockAssets, fallback: null);
        var stockProfile = new AudioProfile
        {
            Id = "stock-default",
            Name = "Stock - Default",
            Assets = stockAssets,
            SlotAssignments = stockAssignments
        };

        var profiles = new List<AudioProfile> { stockProfile };

        profiles.AddRange(BuildCustomProfiles(_importedRoot, "imported", SoundBadge.Imported, stockProfile));
        profiles.AddRange(BuildCustomProfiles(_recordedRoot, "recorded", SoundBadge.Recorded, stockProfile));

        return profiles;
    }

    public async Task<AudioProfile?> GetProfileByIdAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AudioProfile> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException("Sound pack folder was not found.");
        }

        await EnsureAssetRootsAsync(cancellationToken).ConfigureAwait(false);

        var profileId = $"imported-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var destination = Path.Combine(_importedRoot, profileId);
        Directory.CreateDirectory(destination);

        var importedCount = 0;
        foreach (var sourceFile in Directory.GetFiles(folderPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSupportedAudioFile(sourceFile))
            {
                continue;
            }

            var fileName = Path.GetFileName(sourceFile);
            var destinationFile = Path.Combine(destination, fileName);
            File.Copy(sourceFile, destinationFile, overwrite: true);
            importedCount++;
        }

        if (importedCount == 0)
        {
            Directory.Delete(destination, recursive: true);
            throw new InvalidOperationException("No supported audio files were found in the selected sound pack.");
        }

        var profile = await GetProfileByIdAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile ?? throw new InvalidOperationException("Imported profile could not be loaded.");
    }

    public async Task<AudioProfile> RecordClipAsync(SoundSlot slot, CancellationToken cancellationToken = default)
    {
        await EnsureAssetRootsAsync(cancellationToken).ConfigureAwait(false);

        var profileId = "recorded-live";
        var destination = Path.Combine(_recordedRoot, profileId);
        Directory.CreateDirectory(destination);

        var slotTag = SlotToTag(slot);
        var filePath = Path.Combine(destination, $"{slotTag}_recorded_{DateTime.UtcNow:yyyyMMddHHmmss}.wav");

        await CaptureMicrophoneClipAsync(filePath, 1200, cancellationToken).ConfigureAwait(false);

        var profile = await GetProfileByIdAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile ?? throw new InvalidOperationException("Recorded profile could not be loaded.");
    }

    public async Task<bool> DeleteAssetAsync(string profileId, string assetId, CancellationToken cancellationToken = default)
    {
        var profile = await GetProfileByIdAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return false;
        }

        var asset = profile.Assets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, assetId, StringComparison.OrdinalIgnoreCase));

        if (asset is null || !asset.IsDeletable)
        {
            return false;
        }

        if (File.Exists(asset.FilePath))
        {
            File.Delete(asset.FilePath);
        }

        return true;
    }

    private async Task EnsureAssetRootsAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_stockRoot);
            Directory.CreateDirectory(_importedRoot);
            Directory.CreateDirectory(_recordedRoot);

            EnsureStockAsset(SoundSlot.KeyDown, Path.Combine(_stockRoot, "stock_key_down.wav"), 130f, 40, 0.20f);
            EnsureStockAsset(SoundSlot.KeyUp, Path.Combine(_stockRoot, "stock_key_up.wav"), 220f, 28, 0.15f);
            EnsureStockAsset(SoundSlot.MouseLeft, Path.Combine(_stockRoot, "stock_mouse_left.wav"), 170f, 45, 0.22f);
            EnsureStockAsset(SoundSlot.MouseRight, Path.Combine(_stockRoot, "stock_mouse_right.wav"), 210f, 35, 0.18f);
            EnsureStockAsset(SoundSlot.MouseMiddle, Path.Combine(_stockRoot, "stock_mouse_middle.wav"), 250f, 32, 0.14f);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static List<AudioProfile> BuildCustomProfiles(
        string root,
        string profilePrefix,
        SoundBadge badge,
        AudioProfile stockProfile)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var profiles = new List<AudioProfile>();
        foreach (var folder in Directory.GetDirectories(root))
        {
            var folderName = Path.GetFileName(folder);
            var profileId = folderName.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase)
                ? folderName
                : $"{profilePrefix}-{folderName}";

            var customAssets = BuildAssetsFromFolder(profileId, folder, badge);
            if (customAssets.Count == 0)
            {
                continue;
            }

            var mergedAssets = new List<AudioAsset>(stockProfile.Assets.Count + customAssets.Count);
            mergedAssets.AddRange(stockProfile.Assets);
            mergedAssets.AddRange(customAssets);

            var assignments = BuildAssignments(customAssets, stockProfile.SlotAssignments);
            profiles.Add(new AudioProfile
            {
                Id = profileId,
                Name = $"{badge} - {folderName}",
                Assets = mergedAssets,
                SlotAssignments = assignments
            });
        }

        return profiles;
    }

    private static List<AudioAsset> BuildAssetsFromFolder(string profileId, string folder, SoundBadge badge)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var assets = new List<AudioAsset>();
        foreach (var file in Directory.GetFiles(folder))
        {
            if (!IsSupportedAudioFile(file))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(file);
            assets.Add(new AudioAsset
            {
                Id = $"{profileId}:{fileName}".ToLowerInvariant(),
                Name = fileName,
                FilePath = file,
                Badge = badge
            });
        }

        return assets;
    }

    private static Dictionary<SoundSlot, string> BuildAssignments(
        List<AudioAsset> preferredAssets,
        Dictionary<SoundSlot, string>? fallback)
    {
        var assignments = new Dictionary<SoundSlot, string>();

        foreach (var asset in preferredAssets)
        {
            if (!TryResolveSlot(asset.Name, out var slot))
            {
                continue;
            }

            assignments[slot] = asset.Id;
        }

        foreach (var slot in Enum.GetValues<SoundSlot>())
        {
            if (assignments.ContainsKey(slot))
            {
                continue;
            }

            if (fallback is not null && fallback.TryGetValue(slot, out var fallbackAssetId))
            {
                assignments[slot] = fallbackAssetId;
            }
        }

        return assignments;
    }

    private static bool TryResolveSlot(string name, out SoundSlot slot)
    {
        var normalized = name.Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

        if (normalized.Contains("mouse") && normalized.Contains("left"))
        {
            slot = SoundSlot.MouseLeft;
            return true;
        }

        if (normalized.Contains("mouse") && normalized.Contains("right"))
        {
            slot = SoundSlot.MouseRight;
            return true;
        }

        if (normalized.Contains("mouse") && normalized.Contains("middle"))
        {
            slot = SoundSlot.MouseMiddle;
            return true;
        }

        if (normalized.Contains("key") && normalized.Contains("up") || normalized.Contains("release"))
        {
            slot = SoundSlot.KeyUp;
            return true;
        }

        if (normalized.Contains("key") && normalized.Contains("down") || normalized.Contains("press"))
        {
            slot = SoundSlot.KeyDown;
            return true;
        }

        slot = default;
        return false;
    }

    private static bool IsSupportedAudioFile(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string SlotToTag(SoundSlot slot)
    {
        return slot switch
        {
            SoundSlot.KeyDown => "key_down",
            SoundSlot.KeyUp => "key_up",
            SoundSlot.MouseLeft => "mouse_left",
            SoundSlot.MouseRight => "mouse_right",
            SoundSlot.MouseMiddle => "mouse_middle",
            _ => "key_down"
        };
    }

    private static async Task CaptureMicrophoneClipAsync(string filePath, int milliseconds, CancellationToken cancellationToken)
    {
        WaveInEvent? waveIn = null;
        WaveFileWriter? writer = null;
        System.Threading.Timer? stopTimer = null;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                BufferMilliseconds = 20,
                WaveFormat = new WaveFormat(48_000, 1)
            };

            writer = new WaveFileWriter(filePath, waveIn.WaveFormat);

            waveIn.DataAvailable += (_, args) => writer.Write(args.Buffer, 0, args.BytesRecorded);
            waveIn.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                {
                    completion.TrySetException(args.Exception);
                }
                else
                {
                    completion.TrySetResult();
                }
            };

            cancellationToken.Register(() =>
            {
                try
                {
                    waveIn.StopRecording();
                }
                catch
                {
                    // Best-effort cancellation.
                }
            });

            waveIn.StartRecording();
            stopTimer = new System.Threading.Timer(_ => waveIn.StopRecording(), null, milliseconds, Timeout.Infinite);

            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            stopTimer?.Dispose();
            waveIn?.Dispose();
            writer?.Dispose();
        }
    }

    private static void EnsureStockAsset(SoundSlot slot, string path, float baseFrequency, int milliseconds, float noiseMix)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sampleRate = 48_000;
        var totalSamples = (int)(sampleRate * (milliseconds / 1000f));
        var seed = slot switch
        {
            SoundSlot.KeyDown => 11,
            SoundSlot.KeyUp => 23,
            SoundSlot.MouseLeft => 31,
            SoundSlot.MouseRight => 41,
            SoundSlot.MouseMiddle => 53,
            _ => 7
        };

        var random = new Random(seed);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));

        for (var i = 0; i < totalSamples; i++)
        {
            var t = i / (double)sampleRate;
            var envelope = Math.Exp(-9.0 * i / totalSamples);
            var tone = Math.Sin(2.0 * Math.PI * baseFrequency * t);
            var noise = (random.NextDouble() * 2.0) - 1.0;

            var sample = (tone * (1.0 - noiseMix) + (noise * noiseMix)) * envelope * 0.32;
            var value = (float)sample;

            writer.WriteSample(value);
            writer.WriteSample(value);
        }
    }
}
