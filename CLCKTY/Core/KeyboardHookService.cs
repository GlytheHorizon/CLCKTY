using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CLCKTY.Core;

public sealed class KeyboardHookService : IKeyboardHookService
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly HookProc _hookProc;
    private IntPtr _hookHandle;
    private bool _isRunning;

    public KeyboardHookService()
    {
        _hookProc = HookCallback;
    }

    public event EventHandler<KeyboardInputEventArgs>? InputReceived;

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install keyboard hook.");
        }

        _isRunning = true;
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        if (_hookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _isRunning = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = unchecked((int)wParam.ToInt64());
            var isKeyDown = message is WmKeyDown or WmSysKeyDown;
            var isKeyUp = message is WmKeyUp or WmSysKeyUp;

            if (isKeyDown || isKeyUp)
            {
                var hookData = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                InputReceived?.Invoke(
                    this,
                    new KeyboardInputEventArgs((int)hookData.VirtualKeyCode, isKeyDown, message is WmSysKeyDown or WmSysKeyUp));
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VirtualKeyCode;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
