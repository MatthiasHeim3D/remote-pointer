using RemotePointer.Client.Overlays;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Services;

public sealed class ReceiverOverlayService(
    IMonitorService monitorService,
    IDisplayCoordinateMapper coordinateMapper) : IReceiverOverlayService
{
    private ReceiverOverlayWindow? overlay;
    private bool disposed;

    public event EventHandler<OverlayStateChangedEventArgs>? StateChanged;

    public bool IsVisible => overlay?.IsVisible == true;

    public void Show(MonitorDescriptor monitor)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(monitor);

        HideOverlay(raiseEvent: false);

        overlay = new ReceiverOverlayWindow(monitor, monitorService, coordinateMapper);
        overlay.SelectedMonitorDisconnected += OnSelectedMonitorDisconnected;
        overlay.Closed += OnOverlayClosed;
        overlay.Show();

        StateChanged?.Invoke(
            this,
            new OverlayStateChangedEventArgs(
                $"Overlay active on {monitor.Display.DisplayName}.",
                isError: false,
                isVisible: true));
    }

    public void Hide()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        HideOverlay(raiseEvent: true);
    }

    public bool ShowMarker(NormalizedPoint point)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (overlay?.IsVisible != true)
        {
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs(
                    "Show the receiver overlay before testing a marker.",
                    isError: true,
                    isVisible: false));
            return false;
        }

        overlay.ShowMarker(point);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        HideOverlay(raiseEvent: false);
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnSelectedMonitorDisconnected(object? sender, EventArgs e)
    {
        DetachOverlay();
        overlay = null;
        StateChanged?.Invoke(
            this,
            new OverlayStateChangedEventArgs(
                "The selected monitor was disconnected. The overlay has been removed.",
                isError: true,
                isVisible: false));
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, overlay))
        {
            DetachOverlay();
            overlay = null;
        }
    }

    private void HideOverlay(bool raiseEvent)
    {
        if (overlay is not null)
        {
            var window = overlay;
            DetachOverlay();
            overlay = null;
            window.Close();
        }

        if (raiseEvent)
        {
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs(
                    "Overlay hidden.",
                    isError: false,
                    isVisible: false));
        }
    }

    private void DetachOverlay()
    {
        if (overlay is null)
        {
            return;
        }

        overlay.SelectedMonitorDisconnected -= OnSelectedMonitorDisconnected;
        overlay.Closed -= OnOverlayClosed;
    }
}
