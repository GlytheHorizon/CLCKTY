using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CLCKTY.Core;

public sealed class MouseHookService : IMouseHookService
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private readonly HookProc _hookProc;
    private IntPtr _hookHandle;
    private bool _isRunning;

    public MouseHookService()
    {
        _hookProc = HookCallback;
    }

    public event EventHandler<MouseInputEventArgs>? ClickReceived;

    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install mouse hook.");
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
            var button = message switch
            {
                WmLButtonDown => MouseButtonType.Left,
                WmRButtonDown => MouseButtonType.Right,
                WmMButtonDown => MouseButtonType.Middle,
                _ => (MouseButtonType?)null
            };

            if (button.HasValue)
            {
                ClickReceived?.Invoke(this, new MouseInputEventArgs(button.Value));
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsLlHookStruct
    {
        public readonly Point Pt;
        public readonly uint MouseData;
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
