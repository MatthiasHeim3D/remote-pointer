using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace RemoteAnnotate.Client.Services;

public sealed class SystemTrayIcon : IDisposable
{
    private readonly System.Drawing.Icon applicationIcon;
    private readonly FormsContextMenuStrip contextMenu = new();
    private readonly FormsNotifyIcon notifyIcon;
    private bool disposed;

    public SystemTrayIcon(Action showWindow, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(exitApplication);

        var showItem = new FormsToolStripMenuItem("Show Remote Annotate");
        showItem.Click += (_, _) => showWindow();
        var exitItem = new FormsToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitApplication();
        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(exitItem);

        var processPath = Environment.ProcessPath;
        applicationIcon = processPath is null
            ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone()
            : System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();

        notifyIcon = new FormsNotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = applicationIcon,
            Text = "Remote Annotate — Inactive",
            Visible = true,
        };
        notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                showWindow();
            }
        };
    }

    public void SetStatus(string status)
    {
        var text = $"Remote Annotate — {status}";
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
        applicationIcon.Dispose();
        contextMenu.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
