namespace CLCKTY.App.Core;

public interface IKeyboardHookService : IDisposable
{
    event EventHandler<GlobalKeyPressedEventArgs>? KeyDown;
    event EventHandler<GlobalKeyPressedEventArgs>? KeyUp;

    void Start();

    void Stop();
}
