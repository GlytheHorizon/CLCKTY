namespace CLCKTY.App.Core;

public sealed class GlobalMouseButtonEventArgs : EventArgs
{
    public GlobalMouseButtonEventArgs(int inputCode)
    {
        InputCode = inputCode;
    }

    public int InputCode { get; }
}
