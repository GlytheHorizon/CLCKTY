namespace CLCKTY.Core;

public sealed class KeyboardInputEventArgs : EventArgs
{
    public KeyboardInputEventArgs(int virtualKeyCode, bool isKeyDown, bool isSystemKey)
    {
        VirtualKeyCode = virtualKeyCode;
        IsKeyDown = isKeyDown;
        IsSystemKey = isSystemKey;
    }

    public int VirtualKeyCode { get; }

    public bool IsKeyDown { get; }

    public bool IsSystemKey { get; }
}

public sealed class MouseInputEventArgs : EventArgs
{
    public MouseInputEventArgs(MouseButtonType button)
    {
        Button = button;
    }

    public MouseButtonType Button { get; }
}
