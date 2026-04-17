namespace CLCKTY.App.Core;

public sealed class GlobalKeyPressedEventArgs : EventArgs
{
    public GlobalKeyPressedEventArgs(int virtualKey)
    {
        VirtualKey = virtualKey;
    }

    public int VirtualKey { get; }
}
