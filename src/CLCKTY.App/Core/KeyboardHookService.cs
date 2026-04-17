using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CLCKTY.App.Core;

public sealed class KeyboardHookService : IKeyboardHookService
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    private readonly object _sync = new();
    private HookProc? _keyboardHookProc;
    private HookProc? _mouseHookProc;
    private IntPtr _keyboardHookHandle;
    private IntPtr _mouseHookHandle;
    private bool _isStarted;
    private bool _disposed;

    public event EventHandler<GlobalKeyPressedEventArgs>? KeyDown;
    public event EventHandler<GlobalKeyPressedEventArgs>? KeyUp;
    public event EventHandler<GlobalMouseButtonEventArgs>? MouseDown;
    public event EventHandler<GlobalMouseButtonEventArgs>? MouseUp;

    private readonly HashSet<int> _downKeys = new();
    private readonly HashSet<int> _downMouseButtons = new();

    public void Start()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_isStarted)
            {
                return;
            }

            _keyboardHookProc = KeyboardHookCallback;
            _mouseHookProc = MouseHookCallback;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleName = module?.ModuleName;
            var moduleHandle = string.IsNullOrWhiteSpace(moduleName) ? IntPtr.Zero : GetModuleHandle(moduleName);

            _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, moduleHandle, 0);
            if (_keyboardHookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install global keyboard hook.");
            }

            _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseHookProc, moduleHandle, 0);
            if (_mouseHookHandle == IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install global mouse hook.");
            }

            _isStarted = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_isStarted)
            {
                return;
            }

            if (_keyboardHookHandle != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }

            if (_mouseHookHandle != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            _downKeys.Clear();
            _downMouseButtons.Clear();
            _isStarted = false;
            _keyboardHookProc = null;
            _mouseHookProc = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookData = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
            var vk = (int)hookData.VkCode;

            if (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown)
            {
                lock (_sync)
                {
                    if (!_downKeys.Contains(vk))
                    {
                        _downKeys.Add(vk);
                        KeyDown?.Invoke(this, new GlobalKeyPressedEventArgs(vk));
                    }
                }
            }
            else if (wParam == (IntPtr)WmKeyUp || wParam == (IntPtr)WmSysKeyUp)
            {
                lock (_sync)
                {
                    if (_downKeys.Remove(vk))
                    {
                        KeyUp?.Invoke(this, new GlobalKeyPressedEventArgs(vk));
                    }
                }
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookData = Marshal.PtrToStructure<Msllhookstruct>(lParam);

            if (TryTranslateMouseMessage((int)wParam, hookData.MouseData, out var inputCode, out var isDown))
            {
                lock (_sync)
                {
                    if (isDown)
                    {
                        if (_downMouseButtons.Add(inputCode))
                        {
                            MouseDown?.Invoke(this, new GlobalMouseButtonEventArgs(inputCode));
                        }
                    }
                    else
                    {
                        if (_downMouseButtons.Remove(inputCode))
                        {
                            MouseUp?.Invoke(this, new GlobalMouseButtonEventArgs(inputCode));
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private static bool TryTranslateMouseMessage(int message, uint mouseData, out int inputCode, out bool isDown)
    {
        inputCode = 0;
        isDown = false;

        switch (message)
        {
            case WmLButtonDown:
                inputCode = InputBindingCode.MouseLeft;
                isDown = true;
                return true;
            case WmLButtonUp:
                inputCode = InputBindingCode.MouseLeft;
                return true;
            case WmRButtonDown:
                inputCode = InputBindingCode.MouseRight;
                isDown = true;
                return true;
            case WmRButtonUp:
                inputCode = InputBindingCode.MouseRight;
                return true;
            case WmMButtonDown:
                inputCode = InputBindingCode.MouseMiddle;
                isDown = true;
                return true;
            case WmMButtonUp:
                inputCode = InputBindingCode.MouseMiddle;
                return true;
            case WmXButtonDown:
                return TryTranslateXButton(mouseData, true, out inputCode, out isDown);
            case WmXButtonUp:
                return TryTranslateXButton(mouseData, false, out inputCode, out isDown);
            default:
                return false;
        }
    }

    private static bool TryTranslateXButton(uint mouseData, bool down, out int inputCode, out bool isDown)
    {
        var button = (mouseData >> 16) & 0xFFFF;

        if (button == XButton1)
        {
            inputCode = InputBindingCode.MouseX1;
            isDown = down;
            return true;
        }

        if (button == XButton2)
        {
            inputCode = InputBindingCode.MouseX2;
            isDown = down;
            return true;
        }

        inputCode = 0;
        isDown = false;
        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Kbdllhookstruct
    {
        public readonly uint VkCode;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Msllhookstruct
    {
        public readonly Point Pt;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
