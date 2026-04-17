using System.Drawing;
using System.Windows.Forms;

namespace CLCKTY.Services;

public sealed class TrayService : ITrayService
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleSoundsItem;
    private bool _soundsEnabled = true;

    public TrayService()
    {
        _toggleSoundsItem = new ToolStripMenuItem("Toggle Sounds: On");
        _toggleSoundsItem.Click += (_, _) =>
        {
            _soundsEnabled = !_soundsEnabled;
            UpdateToggleText();
            ToggleSoundsRequested?.Invoke(this, _soundsEnabled);
        };

        var openItem = new ToolStripMenuItem("Open");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(_toggleSoundsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "CLCKTY",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? OpenRequested;

    public event EventHandler<bool>? ToggleSoundsRequested;

    public event EventHandler? ExitRequested;

    public void SetSoundState(bool enabled)
    {
        _soundsEnabled = enabled;
        UpdateToggleText();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }

    private void UpdateToggleText()
    {
        _toggleSoundsItem.Text = _soundsEnabled ? "Toggle Sounds: On" : "Toggle Sounds: Off";
    }
}
