using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CLCKTY.App.Core;

public sealed class KeyboardHookService : IKeyboardHookService
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private readonly object _sync = new();
    private HookProc? _hookProc;
    private IntPtr _hookHandle;
    private bool _isStarted;
    private bool _disposed;

    public event EventHandler<GlobalKeyPressedEventArgs>? KeyDown;
    public event EventHandler<GlobalKeyPressedEventArgs>? KeyUp;
    private readonly HashSet<int> _downKeys = new();

    public void Start()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_isStarted)
            {
                return;
            }

            _hookProc = HookCallback;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleName = module?.ModuleName;
            var moduleHandle = string.IsNullOrWhiteSpace(moduleName) ? IntPtr.Zero : GetModuleHandle(moduleName);

            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, moduleHandle, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install global keyboard hook.");
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

            if (_hookHandle != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _isStarted = false;
            _hookProc = null;
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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
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

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
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
