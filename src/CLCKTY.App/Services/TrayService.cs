using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CLCKTY.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _toggleMenuItem;
    private bool _isSoundEnabled = true;
    private bool _disposed;
    private System.Drawing.Icon? _customIcon;
    private IntPtr _customIconHandle;

    public TrayService()
    {
        _toggleMenuItem = new Forms.ToolStripMenuItem();
        _toggleMenuItem.Click += (_, _) =>
        {
            IsSoundEnabled = !IsSoundEnabled;
            SoundsToggled?.Invoke(this, IsSoundEnabled);
        };

        var openMenuItem = new Forms.ToolStripMenuItem("Open");
        openMenuItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(openMenuItem);
        menu.Items.Add(_toggleMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "CLCKTY",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };

        // Use the same logo source and sizing logic as taskbar for consistent branding.
        try
        {
            _customIcon = IconAssetLoader.LoadTrayLogo(out _customIconHandle);
            if (_customIcon is not null)
            {
                _notifyIcon.Icon = _customIcon;
            }
        }
        catch
        {
            // ignore and fall back to default icon
        }

        _notifyIcon.MouseClick += OnNotifyIconMouseClick;

        UpdateToggleMenuText();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler<bool>? SoundsToggled;

    public event EventHandler? ExitRequested;

    public bool IsSoundEnabled
    {
        get => _isSoundEnabled;
        set
        {
            if (_isSoundEnabled == value)
            {
                return;
            }

            _isSoundEnabled = value;
            UpdateToggleMenuText();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_customIcon is not null)
        {
            _customIcon.Dispose();
        }

        if (_customIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_customIconHandle);
            _customIconHandle = IntPtr.Zero;
        }
        _disposed = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void OnNotifyIconMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateToggleMenuText()
    {
        _toggleMenuItem.Text = IsSoundEnabled ? "Toggle Sounds Off" : "Toggle Sounds On";
        _notifyIcon.Text = IsSoundEnabled ? "CLCKTY - Sounds On" : "CLCKTY - Sounds Off";
    }
}
