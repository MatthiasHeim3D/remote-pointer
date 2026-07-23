using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Overlays;

public partial class ReceiverOverlayWindow : Window
{
    private const int MarkerDiameter = 72;
    private const int MarkerDurationMilliseconds = 900;
    private const int MaximumMarkers = 5;

    private readonly IMonitorService monitorService;
    private readonly IDisplayCoordinateMapper coordinateMapper;
    private readonly LinkedList<FrameworkElement> markers = [];
    private readonly PointerVisualRenderer pointerVisuals;
    private readonly string displayId;
    private HwndSource? source;
    private nint handle;
    private MonitorDescriptor monitor;

    public ReceiverOverlayWindow(
        MonitorDescriptor monitor,
        IMonitorService monitorService,
        IDisplayCoordinateMapper coordinateMapper)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        this.coordinateMapper = coordinateMapper ?? throw new ArgumentNullException(nameof(coordinateMapper));
        displayId = monitor.Display.DisplayId;

        InitializeComponent();
        pointerVisuals = new PointerVisualRenderer(MarkerCanvas);
        SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler? SelectedMonitorDisconnected;

    public void ShowPointer(PointerEventMessage pointerEvent)
    {
        ArgumentNullException.ThrowIfNull(pointerEvent);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ShowPointer(pointerEvent));
            return;
        }

        var normalizedPoint = new NormalizedPoint(
            pointerEvent.NormalizedX,
            pointerEvent.NormalizedY);
        if (pointerEvent.Kind is PointerKind.Click or PointerKind.DoubleClick or PointerKind.Attention)
        {
            ShowMarker(normalizedPoint);
            return;
        }

        pointerVisuals.Show(
            pointerEvent.Kind,
            ToOverlayPoint(normalizedPoint),
            pointerEvent.GestureId,
            pointerEvent.Text,
            pointerEvent.PathPoints?
                .Select(ToOverlayPoint)
                .ToArray());
    }

    private void ShowMarker(NormalizedPoint normalizedPoint)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ShowMarker(normalizedPoint));
            return;
        }

        var point = ToOverlayPoint(normalizedPoint);
        var marker = CreateMarker();

        while (markers.Count >= MaximumMarkers)
        {
            RemoveMarker(markers.First!.Value);
        }

        Canvas.SetLeft(marker, point.X - (MarkerDiameter / 2d));
        Canvas.SetTop(marker, point.Y - (MarkerDiameter / 2d));
        MarkerCanvas.Children.Add(marker);
        markers.AddLast(marker);
        AnimateMarker(marker);
    }

    protected override void OnClosed(EventArgs e)
    {
        pointerVisuals.Clear();
        source?.RemoveHook(WindowMessageHook);
        source = null;
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowMessageHook);

        var existingStyles = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var requiredStyles = NativeMethods.WsExLayered
            | NativeMethods.WsExTransparent
            | NativeMethods.WsExNoActivate
            | NativeMethods.WsExToolWindow;
        _ = NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GwlExStyle,
            new nint(existingStyles | requiredStyles));

        ApplyMonitorBounds();
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        _ = window;
        _ = wordParameter;
        _ = longParameter;

        switch (message)
        {
            case NativeMethods.WmNcHitTest:
                handled = true;
                return new nint(NativeMethods.HtTransparent);
            case NativeMethods.WmMouseActivate:
                handled = true;
                return new nint(NativeMethods.MaNoActivate);
            case NativeMethods.WmDisplayChange:
                _ = Dispatcher.InvokeAsync(RefreshMonitor, DispatcherPriority.Background);
                break;
        }

        return 0;
    }

    private void RefreshMonitor()
    {
        MonitorDescriptor? refreshedMonitor;
        try
        {
            refreshedMonitor = monitorService.FindByDisplayId(displayId);
        }
        catch (Win32Exception)
        {
            refreshedMonitor = null;
        }

        if (refreshedMonitor is null)
        {
            SelectedMonitorDisconnected?.Invoke(this, EventArgs.Empty);
            Close();
            return;
        }

        monitor = refreshedMonitor;
        ApplyMonitorBounds();
    }

    private void ApplyMonitorBounds()
    {
        var bounds = monitor.Bounds;
        if (!NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HwndTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The overlay could not be positioned.");
        }
    }

    private Point ToOverlayPoint(NormalizedPoint normalizedPoint)
    {
        var overlayWidth = MarkerCanvas.ActualWidth > 0d
            ? MarkerCanvas.ActualWidth
            : coordinateMapper.PhysicalPixelsToDips(
                monitor.Bounds.Width,
                monitor.Display.ScaleFactor);
        var overlayHeight = MarkerCanvas.ActualHeight > 0d
            ? MarkerCanvas.ActualHeight
            : coordinateMapper.PhysicalPixelsToDips(
                monitor.Bounds.Height,
                monitor.Display.ScaleFactor);
        var point = coordinateMapper.ToOverlayPoint(normalizedPoint, overlayWidth, overlayHeight);
        return new Point(point.X, point.Y);
    }

    private static Grid CreateMarker()
    {
        var marker = new Grid
        {
            Width = MarkerDiameter,
            Height = MarkerDiameter,
            IsHitTestVisible = false,
        };

        var ring = new Ellipse
        {
            Margin = new Thickness(5d),
            Stroke = new SolidColorBrush(Color.FromRgb(255, 92, 92)),
            StrokeThickness = 4d,
            RenderTransformOrigin = new Point(0.5d, 0.5d),
            RenderTransform = new ScaleTransform(0.25d, 0.25d),
        };
        var dot = new Ellipse
        {
            Width = 12d,
            Height = 12d,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(204, 38, 38)),
            StrokeThickness = 3d,
        };

        marker.Children.Add(ring);
        marker.Children.Add(dot);
        return marker;
    }

    private void AnimateMarker(Grid marker)
    {
        var ring = (Ellipse)marker.Children[0];
        var transform = (ScaleTransform)ring.RenderTransform;
        var duration = TimeSpan.FromMilliseconds(MarkerDurationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.25d, 1.15d, duration) { EasingFunction = easing });
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.25d, 1.15d, duration) { EasingFunction = easing });

        var fade = new DoubleAnimation(1d, 0d, duration);
        fade.Completed += (_, _) => RemoveMarker(marker);
        marker.BeginAnimation(OpacityProperty, fade);
    }

    private void RemoveMarker(FrameworkElement marker)
    {
        _ = markers.Remove(marker);
        MarkerCanvas.Children.Remove(marker);
    }
}
