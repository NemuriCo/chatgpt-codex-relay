using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using BlueRelay.Localization;
using WpfApplication = System.Windows.Application;

namespace BlueRelay.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _applicationIcon;
    private readonly MemoryStream _applicationIconStream;
    private readonly Forms.ToolStripMenuItem _alwaysOnTopItem;
    private bool _disposed;

    public TrayService(Action showRequested, Action alwaysOnTopToggleRequested, Action exitRequested, UiTextSet? text = null)
    {
        text ??= LocalizationService.Current;
        _alwaysOnTopItem = new Forms.ToolStripMenuItem(text.AlwaysOnTopEnabled)
        {
            CheckOnClick = true
        };
        _alwaysOnTopItem.Click += (_, _) => alwaysOnTopToggleRequested();

        var showItem = new Forms.ToolStripMenuItem(text.TrayShow);
        showItem.Click += (_, _) => showRequested();

        var exitItem = new Forms.ToolStripMenuItem(text.TrayExit);
        exitItem.Click += (_, _) => exitRequested();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(_alwaysOnTopItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _applicationIconStream = new MemoryStream(LoadApplicationIconBytes());
        _applicationIcon = new Drawing.Icon(_applicationIconStream);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = text.ProductName,
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
        _applicationIcon.Dispose();
        _applicationIconStream.Dispose();
    }

    private static byte[] LoadApplicationIconBytes()
    {
        var resource = WpfApplication.GetResourceStream(new Uri(
            "pack://application:,,,/Assets/Icons/BlueRelay.ico",
            UriKind.Absolute));
        if (resource is null)
        {
            throw new InvalidOperationException("BlueRelay application icon resource was not found.");
        }

        using var stream = resource.Stream;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
