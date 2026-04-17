namespace CLCKTY.App.Core;

public static class InputBindingCode
{
    public const int MouseLeft = 0x1001;
    public const int MouseRight = 0x1002;
    public const int MouseMiddle = 0x1003;
    public const int MouseX1 = 0x1004;
    public const int MouseX2 = 0x1005;

    public static bool IsMouseCode(int inputCode)
    {
        return inputCode >= MouseLeft && inputCode <= MouseX2;
    }
}
