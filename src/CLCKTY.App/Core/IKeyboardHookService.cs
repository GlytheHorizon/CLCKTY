namespace CLCKTY.App.Core;

public interface IKeyboardHookService : IDisposable
{
    event EventHandler<GlobalKeyPressedEventArgs>? KeyPressed;

    void Start();

    void Stop();
}
