using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BlueRelay.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _alwaysOnTopItem;
    private bool _disposed;

    public TrayService(Action showRequested, Action alwaysOnTopToggleRequested, Action exitRequested)
    {
        _alwaysOnTopItem = new Forms.ToolStripMenuItem("Always on top")
        {
            CheckOnClick = true
        };
        _alwaysOnTopItem.Click += (_, _) => alwaysOnTopToggleRequested();

        var showItem = new Forms.ToolStripMenuItem("Show BlueRelay");
        showItem.Click += (_, _) => showRequested();

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitRequested();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(_alwaysOnTopItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "BlueRelay",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => showRequested();
    }

    public void SetAlwaysOnTop(bool value)
    {
        _alwaysOnTopItem.Checked = value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
