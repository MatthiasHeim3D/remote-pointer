using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.Overlays;

public partial class TargetRegionWindow : Window
{
    private const int RippleDurationMilliseconds = 500;
    private const int GestureUpdateIntervalMilliseconds = 16;
    private const int GestureKeepAliveIntervalMilliseconds = 500;

    private readonly RectangleD resetRectangle;
    private readonly PointerVisualRenderer pointerVisuals;
    private readonly DispatcherTimer gestureUpdateTimer;
    private readonly List<Point> pendingPathPoints = [];
    private TextBox? activeTextEditor;
    private Point activeTextPosition;
    private MouseButton? activePointerButton;
    private Point pointerDownPosition;
    private bool pointerDownWithShift;
    private bool isPointerGestureActive;
    private Guid activeGestureId;
    private Point currentGesturePosition;
    private bool gestureUpdatePending;
    private long lastGestureSentAt;
    private bool isPointingMode;
    private bool isResizeDragActive;
    private NativePoint resizeDragStartCursor;
    private double resizeDragStartWidth;
    private double resizeDragStartHeight;
    private double resizeDragDpiScaleX = 1d;
    private double resizeDragDpiScaleY = 1d;

    public TargetRegionWindow(
        RectangleD rectangle,
        RectangleD resetRectangle,
        double expectedAspectRatio,
        bool lockAspectRatio,
        bool showExitHint = true)
    {
        if (!double.IsFinite(expectedAspectRatio) || expectedAspectRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAspectRatio));
        }

        this.resetRectangle = resetRectangle;
        ExpectedAspectRatio = expectedAspectRatio;
        ShowExitHint = showExitHint;

        InitializeComponent();
        pointerVisuals = new PointerVisualRenderer(RippleCanvas);
        gestureUpdateTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(GestureUpdateIntervalMilliseconds),
        };
        gestureUpdateTimer.Tick += OnGestureUpdateTimerTick;
        ApplyRectangle(rectangle);
        AspectLockCheckBox.IsChecked = lockAspectRatio;
        Loaded += (_, _) => UpdateMetrics();
        SizeChanged += (_, _) => UpdateMetrics();
    }

    public event EventHandler<CalibrationLockedEventArgs>? CalibrationLocked;

    public event EventHandler? CalibrationCancelled;

    public event EventHandler? PointingExitRequested;

    public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    public double ExpectedAspectRatio { get; }

    public bool ShowExitHint { get; }

    public void EnterPointingMode()
    {
        isPointingMode = true;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        PointingExitHint.Visibility = ShowExitHint ? Visibility.Visible : Visibility.Collapsed;
        ResizeThumb.Visibility = Visibility.Collapsed;
        OuterBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 92, 92));
        OuterBorder.BorderThickness = new Thickness(2d);
        OuterBorder.Background = Brushes.Transparent;
        // A zero-alpha pixel in an AllowsTransparency window can be omitted from the
        // layered window's input surface. Alpha 1 is visually imperceptible but keeps
        // the complete calibrated rectangle available for hit testing.
        TargetSurface.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        EditorCanvas.IsHitTestVisible = true;
        Cursor = Cursors.Cross;
    }

    private void OnDragSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (isPointingMode || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        UpdateMetrics();
        e.Handled = true;
    }

    private void OnResizeThumbDragStarted(
        object sender,
        System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (isPointingMode || !NativeMethods.GetCursorPos(out resizeDragStartCursor))
        {
            isResizeDragActive = false;
            return;
        }

        resizeDragStartWidth = ActualWidth > 0d ? ActualWidth : Width;
        resizeDragStartHeight = ActualHeight > 0d ? ActualHeight : Height;
        var dpi = VisualTreeHelper.GetDpi(this);
        resizeDragDpiScaleX = dpi.DpiScaleX;
        resizeDragDpiScaleY = dpi.DpiScaleY;
        isResizeDragActive = true;
    }

    private void OnResizeThumbDragDelta(
        object sender,
        System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (isPointingMode ||
            !isResizeDragActive ||
            !NativeMethods.GetCursorPos(out var currentCursor))
        {
            return;
        }

        var horizontalChange = (currentCursor.X - (double)resizeDragStartCursor.X) / resizeDragDpiScaleX;
        var verticalChange = (currentCursor.Y - (double)resizeDragStartCursor.Y) / resizeDragDpiScaleY;
        var resized = TargetRegionGeometry.Resize(
            resizeDragStartWidth,
            resizeDragStartHeight,
            horizontalChange,
            verticalChange,
            ExpectedAspectRatio,
            AspectLockCheckBox.IsChecked == true);
        Width = resized.X;
        Height = resized.Y;
        UpdateMetrics();
        e.Handled = true;
    }

    private void OnResizeThumbDragCompleted(
        object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        isResizeDragActive = false;
    }

    private void OnAspectLockChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || isPointingMode)
        {
            return;
        }

        if (AspectLockCheckBox.IsChecked != true)
        {
            UpdateMetrics();
            return;
        }

        var width = Math.Max(TargetRegionGeometry.MinimumWidth, ActualWidth > 0d ? ActualWidth : Width);
        var height = width / ExpectedAspectRatio;
        if (height < TargetRegionGeometry.MinimumHeight)
        {
            height = TargetRegionGeometry.MinimumHeight;
            width = height * ExpectedAspectRatio;
        }

        Width = width;
        Height = height;
        UpdateMetrics();
    }

    private void OnLockClicked(object sender, RoutedEventArgs e)
    {
        var width = ActualWidth > 0d ? ActualWidth : Width;
        var height = ActualHeight > 0d ? ActualHeight : Height;
        CalibrationLocked?.Invoke(
            this,
            new CalibrationLockedEventArgs(new RectangleD(Left, Top, width, height)));
        e.Handled = true;
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        ApplyRectangle(resetRectangle);
        UpdateMetrics();
        e.Handled = true;
    }

    private void OnFullscreenClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentMonitorBounds(out var monitorBounds))
        {
            e.Handled = true;
            return;
        }

        ApplyRectangle(TargetRegionGeometry.FitWithin(
            monitorBounds,
            ExpectedAspectRatio,
            AspectLockCheckBox.IsChecked == true));
        UpdateMetrics();
        e.Handled = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CalibrationCancelled?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (isPointingMode && activeTextEditor is not null)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                FinalizeTextEditor();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RemoveTextEditor();
                _ = Focus();
            }

            return;
        }

        if (isPointingMode && e.Key == Key.Escape)
        {
            e.Handled = true;
            PointingExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!isPointingMode || activeTextEditor is not null || activePointerButton is not null)
        {
            return;
        }

        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right))
        {
            return;
        }

        e.Handled = true;
        activePointerButton = e.ChangedButton;
        pointerDownPosition = e.GetPosition(TargetSurface);
        pointerDownWithShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        isPointerGestureActive = false;
        activeGestureId = Guid.Empty;
        pendingPathPoints.Clear();
        gestureUpdatePending = false;
        gestureUpdateTimer.Stop();
        _ = CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!isPointingMode || activePointerButton is null || activeTextEditor is not null)
        {
            return;
        }

        e.Handled = true;
        var current = e.GetPosition(TargetSurface);
        if (!isPointerGestureActive)
        {
            var horizontalDistance = Math.Abs(current.X - pointerDownPosition.X);
            var verticalDistance = Math.Abs(current.Y - pointerDownPosition.Y);
            if (horizontalDistance < SystemParameters.MinimumHorizontalDragDistance
                && verticalDistance < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            isPointerGestureActive = true;
            activeGestureId = Guid.NewGuid();
            var startKind = GetGestureKind(start: true, end: false);
            pointerVisuals.Show(startKind, pointerDownPosition, activeGestureId, text: null);
            RaisePointerCaptured(pointerDownPosition, startKind, activeGestureId);
            currentGesturePosition = pointerDownPosition;
            lastGestureSentAt = Environment.TickCount64;
            gestureUpdateTimer.Start();
        }

        var updateKind = GetGestureKind(start: false, end: false);
        pointerVisuals.Show(updateKind, current, activeGestureId, text: null);
        QueueGestureUpdate(current);
        if (pendingPathPoints.Count >= ContractValidator.MaximumPathPointsPerEvent)
        {
            FlushGestureUpdate();
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!isPointingMode || activePointerButton != e.ChangedButton)
        {
            return;
        }

        e.Handled = true;
        var releasePosition = e.GetPosition(TargetSurface);
        ReleaseMouseCapture();
        gestureUpdateTimer.Stop();

        if (isPointerGestureActive)
        {
            var endKind = GetGestureKind(start: false, end: true);
            pointerVisuals.Show(endKind, releasePosition, activeGestureId, text: null);
            NormalizedPoint[]? pathPoints = null;
            if (IsPathGesture())
            {
                AddPendingPathPoint(releasePosition);
                pathPoints = NormalizePathPoints(pendingPathPoints);
            }

            RaisePointerCaptured(
                releasePosition,
                endKind,
                activeGestureId,
                pathPoints: pathPoints);
        }
        else if (activePointerButton == MouseButton.Left && pointerDownWithShift)
        {
            OpenTextEditor(pointerDownPosition);
        }
        else if (activePointerButton == MouseButton.Left)
        {
            ShowRipple(pointerDownPosition);
            RaisePointerCaptured(pointerDownPosition, PointerKind.Click);
        }

        activePointerButton = null;
        isPointerGestureActive = false;
        activeGestureId = Guid.Empty;
        pendingPathPoints.Clear();
        gestureUpdatePending = false;
    }

    private void OnGestureUpdateTimerTick(object? sender, EventArgs e)
    {
        if (!isPointerGestureActive || activePointerButton is null)
        {
            gestureUpdateTimer.Stop();
            return;
        }

        var now = Environment.TickCount64;
        if (gestureUpdatePending
            || now - lastGestureSentAt >= GestureKeepAliveIntervalMilliseconds)
        {
            FlushGestureUpdate();
        }
    }

    private void QueueGestureUpdate(Point point)
    {
        currentGesturePosition = point;
        gestureUpdatePending = true;
        if (IsPathGesture())
        {
            AddPendingPathPoint(point);
        }
    }

    private void AddPendingPathPoint(Point point)
    {
        if (pendingPathPoints.Count == 0 || pendingPathPoints[^1] != point)
        {
            pendingPathPoints.Add(point);
        }
    }

    private void FlushGestureUpdate()
    {
        if (!isPointerGestureActive || activePointerButton is null)
        {
            return;
        }

        var updateKind = GetGestureKind(start: false, end: false);
        var pathPoints = IsPathGesture()
            ? NormalizePathPoints(pendingPathPoints)
            : null;

        // Refreshing the active visual also keeps it alive while the pointer is held still.
        pointerVisuals.Show(
            updateKind,
            currentGesturePosition,
            activeGestureId,
            text: null,
            IsPathGesture() ? Array.Empty<Point>() : null);
        RaisePointerCaptured(
            currentGesturePosition,
            updateKind,
            activeGestureId,
            pathPoints: pathPoints);
        pendingPathPoints.Clear();
        gestureUpdatePending = false;
        lastGestureSentAt = Environment.TickCount64;
    }

    private bool IsPathGesture() =>
        activePointerButton == MouseButton.Left && !pointerDownWithShift;

    private PointerKind GetGestureKind(bool start, bool end) => GetGestureKind(
        activePointerButton ?? throw new InvalidOperationException("No pointer gesture is active."),
        pointerDownWithShift,
        start,
        end);

    internal static PointerKind GetGestureKind(
        MouseButton pointerButton,
        bool withShift,
        bool start,
        bool end)
    {
        if (pointerButton == MouseButton.Right)
        {
            if (withShift)
            {
                return start
                    ? PointerKind.CircleStart
                    : end ? PointerKind.CircleEnd : PointerKind.CircleUpdate;
            }

            return start
                ? PointerKind.RectangleStart
                : end ? PointerKind.RectangleEnd : PointerKind.RectangleUpdate;
        }

        if (withShift)
        {
            return start
                ? PointerKind.LineStart
                : end ? PointerKind.LineEnd : PointerKind.LineUpdate;
        }

        return start
            ? PointerKind.PathStart
            : end ? PointerKind.PathEnd : PointerKind.PathUpdate;
    }

    private void RaisePointerCaptured(
        Point position,
        PointerKind kind,
        Guid? gestureId = null,
        string? text = null,
        NormalizedPoint[]? pathPoints = null)
    {
        var width = Math.Max(1d, TargetSurface.ActualWidth);
        var height = Math.Max(1d, TargetSurface.ActualHeight);
        var point = CoordinateMapper.Normalize(
            new PointD(position.X, position.Y),
            new RectangleD(0d, 0d, width, height));

        PointerCaptured?.Invoke(
            this,
            new PointerCapturedEventArgs(point, kind, gestureId, text, pathPoints));
    }

    private NormalizedPoint[] NormalizePathPoints(IReadOnlyList<Point> points)
    {
        var width = Math.Max(1d, TargetSurface.ActualWidth);
        var height = Math.Max(1d, TargetSurface.ActualHeight);
        var rectangle = new RectangleD(0d, 0d, width, height);
        return points
            .Select(point => CoordinateMapper.Normalize(new PointD(point.X, point.Y), rectangle))
            .ToArray();
    }

    private void OpenTextEditor(Point position)
    {
        RemoveTextEditor();
        activeTextPosition = position;
        activeTextEditor = new TextBox
        {
            Width = 260d,
            MaxLength = ContractValidator.MaximumPointerTextLength,
            Padding = new Thickness(8d, 5d, 8d, 5d),
            FontSize = 17d,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(240, 17, 23, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 92, 92)),
            BorderThickness = new Thickness(2d),
            CaretBrush = Brushes.White,
            AcceptsReturn = false,
        };
        Canvas.SetLeft(activeTextEditor, position.X + 14d);
        Canvas.SetTop(activeTextEditor, position.Y - 8d);
        EditorCanvas.Children.Add(activeTextEditor);
        Cursor = Cursors.Arrow;
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                _ = activeTextEditor?.Focus();
                if (activeTextEditor is not null)
                {
                    Keyboard.Focus(activeTextEditor);
                }
            });
    }

    private void FinalizeTextEditor()
    {
        if (activeTextEditor is null)
        {
            return;
        }

        var text = activeTextEditor.Text.Trim();
        var position = activeTextPosition;
        RemoveTextEditor();
        _ = Focus();
        if (text.Length == 0)
        {
            return;
        }

        pointerVisuals.Show(PointerKind.Text, position, gestureId: null, text);
        RaisePointerCaptured(position, PointerKind.Text, text: text);
    }

    private void RemoveTextEditor()
    {
        if (activeTextEditor is not null)
        {
            EditorCanvas.Children.Remove(activeTextEditor);
            activeTextEditor = null;
        }

        Cursor = Cursors.Cross;
    }

    protected override void OnClosed(EventArgs e)
    {
        gestureUpdateTimer.Stop();
        gestureUpdateTimer.Tick -= OnGestureUpdateTimerTick;
        pointerVisuals.Clear();
        base.OnClosed(e);
    }

    private void UpdateMetrics()
    {
        if (!IsInitialized || isPointingMode)
        {
            return;
        }

        var width = ActualWidth > 0d ? ActualWidth : Width;
        var height = ActualHeight > 0d ? ActualHeight : Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0d || height <= 0d)
        {
            return;
        }

        var differs = AspectLockCheckBox.IsChecked != true
            && AspectRatio.ExceedsTolerance(
                AspectRatio.Calculate(width, height),
                ExpectedAspectRatio);
        AspectWarningPanel.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRectangle(RectangleD rectangle)
    {
        Left = rectangle.Left;
        Top = rectangle.Top;
        Width = rectangle.Width;
        Height = rectangle.Height;
    }

    private bool TryGetCurrentMonitorBounds(out RectangleD bounds)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        var monitorHandle = NativeMethods.MonitorFromWindow(
            windowHandle,
            NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
        };

        if (monitorHandle == 0 || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            bounds = default;
            return false;
        }

        var topLeft = PointFromScreen(new Point(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top));
        var bottomRight = PointFromScreen(new Point(monitorInfo.Monitor.Right, monitorInfo.Monitor.Bottom));
        bounds = new RectangleD(
            Left + topLeft.X,
            Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
        return bounds.Width > 0d && bounds.Height > 0d;
    }

    private void ShowRipple(System.Windows.Point click)
    {
        const double diameter = 54d;
        var ring = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 92, 92)),
            StrokeThickness = 4d,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5d, 0.5d),
            RenderTransform = new ScaleTransform(0.25d, 0.25d),
        };
        Canvas.SetLeft(ring, click.X - (diameter / 2d));
        Canvas.SetTop(ring, click.Y - (diameter / 2d));
        RippleCanvas.Children.Add(ring);

        var duration = TimeSpan.FromMilliseconds(RippleDurationMilliseconds);
        var transform = (ScaleTransform)ring.RenderTransform;
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.25d, 1.1d, duration));
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.25d, 1.1d, duration));
        var fade = new DoubleAnimation(1d, 0d, duration);
        fade.Completed += (_, _) => RippleCanvas.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade);
    }
}
