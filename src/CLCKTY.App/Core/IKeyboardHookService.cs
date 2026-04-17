namespace CLCKTY.App.Core;

public interface IKeyboardHookService : IDisposable
{
    event EventHandler<GlobalKeyPressedEventArgs>? KeyDown;
    event EventHandler<GlobalKeyPressedEventArgs>? KeyUp;
    event EventHandler<GlobalMouseButtonEventArgs>? MouseDown;
    event EventHandler<GlobalMouseButtonEventArgs>? MouseUp;

    void Start();

    void Stop();
}
