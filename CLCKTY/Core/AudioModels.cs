namespace CLCKTY.Core;

public enum SoundBadge
{
    Stock,
    Imported,
    Recorded
}

public enum SoundSlot
{
    KeyDown,
    KeyUp,
    MouseLeft,
    MouseRight,
    MouseMiddle
}

public enum MouseButtonType
{
    Left,
    Right,
    Middle
}

public enum LatencyMode
{
    Balanced,
    Performance
}

public sealed class AudioAsset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string FilePath { get; init; }

    public SoundBadge Badge { get; init; }

    public bool IsDeletable => Badge != SoundBadge.Stock;
}

public sealed class AudioProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public Dictionary<SoundSlot, string> SlotAssignments { get; init; } = new();

    public List<AudioAsset> Assets { get; init; } = new();

    public AudioAsset? ResolveAsset(SoundSlot slot)
    {
        if (!SlotAssignments.TryGetValue(slot, out var assetId))
        {
            return null;
        }

        return Assets.FirstOrDefault(asset => string.Equals(asset.Id, assetId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SoundEngineConfiguration
{
    public bool SoundsEnabled { get; init; } = true;

    public bool SpatialAudioEnabled { get; init; } = true;

    public bool RandomPitchEnabled { get; init; } = true;

    public bool KeyDownEnabled { get; init; } = true;

    public bool KeyUpEnabled { get; init; } = true;

    public bool MouseLeftEnabled { get; init; } = true;

    public bool MouseRightEnabled { get; init; } = true;

    public bool MouseMiddleEnabled { get; init; } = true;

    public float MasterVolume { get; init; } = 0.85f;

    public float ToneX { get; init; }

    public float ToneY { get; init; }

    public LatencyMode LatencyMode { get; init; } = LatencyMode.Performance;
}
