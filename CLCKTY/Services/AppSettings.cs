using CLCKTY.Core;

namespace CLCKTY.Services;

public sealed class AppSettings
{
    public bool SoundsEnabled { get; set; } = true;

    public bool SpatialAudioEnabled { get; set; } = true;

    public bool RandomPitchEnabled { get; set; } = true;

    public bool KeyDownEnabled { get; set; } = true;

    public bool KeyUpEnabled { get; set; } = true;

    public bool MouseLeftEnabled { get; set; } = true;

    public bool MouseRightEnabled { get; set; } = true;

    public bool MouseMiddleEnabled { get; set; } = true;

    public double MasterVolume { get; set; } = 0.85;

    public double ToneX { get; set; } = -0.25;

    public double ToneY { get; set; }

    public string ActiveProfileId { get; set; } = "stock-default";

    public bool StartWithWindows { get; set; }

    public LatencyMode LatencyMode { get; set; } = LatencyMode.Performance;

    public bool GlobalHotkeyEnabled { get; set; } = true;

    public string ToggleHotkey { get; set; } = "Ctrl+Alt+M";

    public bool StartMinimizedToTray { get; set; } = true;
}
