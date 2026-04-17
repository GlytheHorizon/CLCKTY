namespace CLCKTY.Services;

public interface ITrayService : IDisposable
{
    event EventHandler? OpenRequested;

    event EventHandler<bool>? ToggleSoundsRequested;

    event EventHandler? ExitRequested;

    void SetSoundState(bool enabled);
}
