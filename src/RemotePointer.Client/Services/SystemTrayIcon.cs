using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace RemotePointer.Client.Services;

public sealed class SystemTrayIcon : IDisposable
{
    private readonly FormsContextMenuStrip contextMenu = new();
    private readonly FormsNotifyIcon notifyIcon;
    private bool disposed;

    public SystemTrayIcon(Action showWindow, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(exitApplication);

        var showItem = new FormsToolStripMenuItem("Show Remote Pointer");
        showItem.Click += (_, _) => showWindow();
        var exitItem = new FormsToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitApplication();
        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(exitItem);

        notifyIcon = new FormsNotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Remote Pointer — Inactive",
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void SetStatus(string status)
    {
        var text = $"Remote Pointer — {status}";
        notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        contextMenu.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
