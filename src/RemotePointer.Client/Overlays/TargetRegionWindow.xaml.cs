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

    /// <summary>
    /// The window's own chrome — the frame around the annotation area and the box text is typed
    /// into — stays the client's accent whatever colour the annotator draws in. It marks where
    /// the tool is rather than being part of a drawing, and letting it follow the colour makes
    /// the chosen colour harder to judge against the shape actually drawn in it.
    /// </summary>
    private static readonly SolidColorBrush ChromeAccentBrush =
        AnnotationPalette.CreateStrokeBrush(AnnotationPalette.DefaultAccent);

    private readonly RectangleD resetRectangle;
    private SolidColorBrush annotationBrush;
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
    private bool isAnnotatingMode;
    private bool isAnnotationPaused;
    private bool isUsageHelpCollapsed;
    private bool isResizeDragActive;
    private bool isResizeRenderHooked;
    private RectangleD? pendingResizeRectangle;
    private PhysicalRectangle? lastAppliedPlacement;
    private TargetRegionCorner resizeDragCorner;
    private NativePoint resizeDragStartCursor;
    private double resizeDragStartLeft;
    private double resizeDragStartTop;
    private double resizeDragStartWidth;
    private double resizeDragStartHeight;
    private double resizeDragDpiScaleX = 1d;
    private double resizeDragDpiScaleY = 1d;

    public TargetRegionWindow(
        RectangleD rectangle,
        RectangleD resetRectangle,
        double expectedAspectRatio,
        bool lockAspectRatio,
        bool showUsageHints = true,
        bool expandUsageHintsInitially = false,
        double drawingOpacity = 1d,
        string? annotationColor = null)
    {
        if (!double.IsFinite(expectedAspectRatio) || expectedAspectRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAspectRatio));
        }

        this.resetRectangle = resetRectangle;
        ExpectedAspectRatio = expectedAspectRatio;
        ShowUsageHints = showUsageHints;
        ExpandUsageHintsInitially = expandUsageHintsInitially;
        DrawingOpacity = double.IsFinite(drawingOpacity)
            ? Math.Clamp(drawingOpacity, 0d, 1d)
            : 1d;
        AnnotationColor = AnnotationPalette.ToColor(annotationColor);
        annotationBrush = AnnotationPalette.CreateStrokeBrush(AnnotationColor);

        InitializeComponent();
        // Every shape the annotator draws lives on this canvas, so one canvas-level opacity
        // multiplies through them without touching the fade animations on the shapes.
        RippleCanvas.Opacity = DrawingOpacity;
        pointerVisuals = new PointerVisualRenderer(
            RippleCanvas,
            defaultAccent: AnnotationColor);
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

    public event EventHandler? AnnotatingExitRequested;

    public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    public double ExpectedAspectRatio { get; }

    public bool ShowUsageHints { get; }

    public bool ExpandUsageHintsInitially { get; }

    public double DrawingOpacity { get; }

    /// <summary>
    /// The colour this annotator draws in. It reaches the drawings only — shapes, freehand ink,
    /// click ripples and placed text notes — and not the window's own chrome. The same value
    /// travels with every pointer event, so the host's copy of a drawing matches this one.
    /// </summary>
    public Color AnnotationColor { get; private set; }

    /// <summary>
    /// Recolours what is drawn from here on, without closing the window, so a colour picked
    /// during a live session takes effect on the next stroke rather than the next calibration.
    /// Shapes already on the canvas keep the colour they were drawn in.
    /// </summary>
    public void SetAnnotationColor(string? annotationColor)
    {
        var accent = AnnotationPalette.ToColor(annotationColor);
        if (accent == AnnotationColor)
        {
            return;
        }

        AnnotationColor = accent;
        annotationBrush = AnnotationPalette.CreateStrokeBrush(accent);
        pointerVisuals.SetDefaultAccent(accent);
    }

    public void EnterAnnotatingMode()
    {
        isAnnotatingMode = true;
        isResizeDragActive = false;
        pendingResizeRectangle = null;
        UnhookResizeRendering();
        isUsageHelpCollapsed = !ExpandUsageHintsInitially;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        UpdateUsageHelpVisibility();
        OuterBorder.BorderBrush = ChromeAccentBrush;
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
        if (isAnnotatingMode || e.ChangedButton != MouseButton.Left)
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
        if (isAnnotatingMode
            || !TryGetResizeCorner(sender, out resizeDragCorner)
            || !NativeMethods.GetCursorPos(out resizeDragStartCursor))
        {
            isResizeDragActive = false;
            return;
        }

        resizeDragStartLeft = Left;
        resizeDragStartTop = Top;
        resizeDragStartWidth = ActualWidth > 0d ? ActualWidth : Width;
        resizeDragStartHeight = ActualHeight > 0d ? ActualHeight : Height;
        var dpi = VisualTreeHelper.GetDpi(this);
        resizeDragDpiScaleX = dpi.DpiScaleX;
        resizeDragDpiScaleY = dpi.DpiScaleY;
        isResizeDragActive = true;
        pendingResizeRectangle = null;
        lastAppliedPlacement = null;
        // Mouse moves arrive far faster than the screen refreshes, and every one of them would
        // otherwise repaint this layered window end to end. Collapsing them onto the render
        // loop keeps the drag at one resize per displayed frame.
        if (!isResizeRenderHooked)
        {
            isResizeRenderHooked = true;
            CompositionTarget.Rendering += OnResizeRendering;
        }
    }

    private void OnResizeThumbDragDelta(
        object sender,
        System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (isAnnotatingMode ||
            !isResizeDragActive ||
            !NativeMethods.GetCursorPos(out var currentCursor))
        {
            return;
        }

        var horizontalChange = (currentCursor.X - (double)resizeDragStartCursor.X) / resizeDragDpiScaleX;
        var verticalChange = (currentCursor.Y - (double)resizeDragStartCursor.Y) / resizeDragDpiScaleY;
        var resized = TargetRegionGeometry.ResizeFromCorner(
            new RectangleD(
                resizeDragStartLeft,
                resizeDragStartTop,
                resizeDragStartWidth,
                resizeDragStartHeight),
            resizeDragCorner,
            horizontalChange,
            verticalChange,
            ExpectedAspectRatio,
            AspectLockCheckBox.IsChecked == true);
        pendingResizeRectangle = resized;
        e.Handled = true;
    }

    private void OnResizeRendering(object? sender, EventArgs e)
    {
        if (pendingResizeRectangle is not { } rectangle)
        {
            return;
        }

        pendingResizeRectangle = null;
        ApplyRectangleWhileDragging(rectangle);
    }

    /// <summary>
    /// Moves and sizes the window in a single native call. Assigning
    /// <see cref="Window.Left"/>, <see cref="Window.Top"/>, <see cref="FrameworkElement.Width"/>
    /// and <see cref="FrameworkElement.Height"/> instead costs four separate window placements,
    /// which a corner drag shows as the opposite edge jittering while the window catches up.
    /// </summary>
    private void ApplyRectangleWhileDragging(RectangleD rectangle)
    {
        var placement = GetDevicePlacement(
            rectangle,
            resizeDragDpiScaleX,
            resizeDragDpiScaleY);

        // Sub-pixel cursor movement rounds to the placement the window already has. Repainting
        // a layered window this size is far too expensive to spend on a no-op.
        if (placement == lastAppliedPlacement)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0
            || !NativeMethods.SetWindowPos(
                handle,
                insertAfter: 0,
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                // The surface is repainted from scratch anyway, so preserving the old pixels
                // and erasing the background are both wasted work that shows up as smearing.
                NativeMethods.SwpNoZOrder
                    | NativeMethods.SwpNoActivate
                    | NativeMethods.SwpNoCopyBits
                    | NativeMethods.SwpDeferErase))
        {
            ApplyRectangle(rectangle);
            return;
        }

        lastAppliedPlacement = placement;
    }

    /// <summary>
    /// Converts a rectangle to device pixels by rounding its four edges rather than its origin
    /// and its size. Rounding origin and size separately lets the two disagree, which walks the
    /// edge opposite the drag back and forth by a pixel from one frame to the next; rounding
    /// edges pins that edge to the same pixel for the whole drag.
    /// </summary>
    internal static PhysicalRectangle GetDevicePlacement(
        RectangleD rectangle,
        double scaleX,
        double scaleY)
    {
        var left = (int)Math.Round(rectangle.Left * scaleX);
        var top = (int)Math.Round(rectangle.Top * scaleY);
        var right = (int)Math.Round((rectangle.Left + rectangle.Width) * scaleX);
        var bottom = (int)Math.Round((rectangle.Top + rectangle.Height) * scaleY);

        return new PhysicalRectangle(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Brings the dependency properties back in step with the window that was placed natively,
    /// so later reads of <see cref="Window.Left"/> and friends see where the window really is.
    /// </summary>
    private void SyncPlacementFromWindowHandle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0
            || !NativeMethods.GetWindowRect(handle, out var bounds)
            || resizeDragDpiScaleX <= 0d
            || resizeDragDpiScaleY <= 0d)
        {
            return;
        }

        ApplyRectangle(new RectangleD(
            bounds.Left / resizeDragDpiScaleX,
            bounds.Top / resizeDragDpiScaleY,
            (bounds.Right - bounds.Left) / resizeDragDpiScaleX,
            (bounds.Bottom - bounds.Top) / resizeDragDpiScaleY));
    }

    private static bool TryGetResizeCorner(object sender, out TargetRegionCorner corner)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            return Enum.TryParse(tag, out corner);
        }

        corner = default;
        return false;
    }

    private void OnResizeThumbDragCompleted(
        object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        UnhookResizeRendering();
        var wasDragging = isResizeDragActive;
        isResizeDragActive = false;

        // Without an accepted drag there is no captured DPI scale to convert with, and nothing
        // to flush either.
        if (!wasDragging)
        {
            pendingResizeRectangle = null;
            return;
        }

        if (pendingResizeRectangle is { } rectangle)
        {
            pendingResizeRectangle = null;
            ApplyRectangleWhileDragging(rectangle);
        }

        SyncPlacementFromWindowHandle();
        UpdateMetrics();
    }

    private void UnhookResizeRendering()
    {
        if (isResizeRenderHooked)
        {
            isResizeRenderHooked = false;
            CompositionTarget.Rendering -= OnResizeRendering;
        }
    }

    private void OnAspectLockChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || isAnnotatingMode)
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
        if (isAnnotatingMode && activeTextEditor is not null)
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

        if (isAnnotatingMode && e.Key == Key.Escape)
        {
            e.Handled = true;
            AnnotatingExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (isAnnotatingMode && e.Key == Key.H)
        {
            e.Handled = true;
            isUsageHelpCollapsed = !isUsageHelpCollapsed;
            UpdateUsageHelpVisibility();
        }
    }

    private void UpdateUsageHelpVisibility()
    {
        var (usageHelp, collapsedHint) = GetUsageHintVisibilities(
            ShowUsageHints,
            isUsageHelpCollapsed);
        AnnotatingUsageHint.Visibility = usageHelp;
        AnnotatingUsageCollapsedHint.Visibility = collapsedHint;
    }

    internal static (Visibility UsageHelp, Visibility CollapsedHint)
        GetUsageHintVisibilities(bool showCollapsedHint, bool isCollapsed) =>
        (
            isCollapsed ? Visibility.Collapsed : Visibility.Visible,
            showCollapsedHint && isCollapsed ? Visibility.Visible : Visibility.Collapsed
        );

    /// <summary>
    /// Shows or clears the paused state the host controls. Whatever was being drawn when the
    /// pause arrived is dropped rather than finished, because its closing event would never
    /// reach the host.
    /// </summary>
    public void SetAnnotationPaused(bool paused)
    {
        if (isAnnotationPaused == paused)
        {
            return;
        }

        isAnnotationPaused = paused;
        PausedOverlay.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        if (paused)
        {
            AbandonActiveGesture();
        }

        if (isAnnotatingMode)
        {
            Cursor = paused ? Cursors.No : Cursors.Cross;
        }
    }

    private void AbandonActiveGesture()
    {
        RemoveTextEditor();
        if (activePointerButton is not null)
        {
            ReleaseMouseCapture();
            activePointerButton = null;
        }

        gestureUpdateTimer.Stop();
        isPointerGestureActive = false;
        activeGestureId = Guid.Empty;
        pendingPathPoints.Clear();
        gestureUpdatePending = false;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!isAnnotatingMode || isAnnotationPaused || activePointerButton is not null)
        {
            return;
        }

        if (activeTextEditor is not null)
        {
            if (activeTextEditor.IsMouseOver)
            {
                return;
            }

            e.Handled = true;
            RemoveTextEditor();
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
        if (!isAnnotatingMode
            || isAnnotationPaused
            || activePointerButton is null
            || activeTextEditor is not null)
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
        if (!isAnnotatingMode || isAnnotationPaused || activePointerButton != e.ChangedButton)
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
            BorderBrush = ChromeAccentBrush,
            BorderThickness = new Thickness(2d),
            CaretBrush = Brushes.White,
            AcceptsReturn = false,
        };
        activeTextEditor.LostKeyboardFocus += OnActiveTextEditorLostKeyboardFocus;
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
            activeTextEditor.LostKeyboardFocus -= OnActiveTextEditorLostKeyboardFocus;
            EditorCanvas.Children.Remove(activeTextEditor);
            activeTextEditor = null;
        }

        Cursor = Cursors.Cross;
    }

    private void OnActiveTextEditorLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RemoveTextEditor();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnhookResizeRendering();
        gestureUpdateTimer.Stop();
        gestureUpdateTimer.Tick -= OnGestureUpdateTimerTick;
        pointerVisuals.Clear();
        base.OnClosed(e);
    }

    private void UpdateMetrics()
    {
        if (!IsInitialized || isAnnotatingMode)
        {
            return;
        }

        var width = ActualWidth > 0d ? ActualWidth : Width;
        var height = ActualHeight > 0d ? ActualHeight : Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0d || height <= 0d)
        {
            return;
        }

        // The long description is the first thing to go when the target area gets small, so the
        // move handle keeps its room for as long as possible.
        CalibrationDescription.Visibility = GetDescriptionVisibility(
            width,
            height,
            CalibrationDescription.Visibility);

        var freeHeight = height - CalibrationHeader.ActualHeight - CalibrationControls.ActualHeight;
        var handleSize = GetMoveHandleSize(width, freeHeight);
        if (handleSize <= 0d)
        {
            DragSurface.Visibility = Visibility.Collapsed;
        }
        else
        {
            DragSurface.Visibility = Visibility.Visible;
            if (DragSurface.Width != handleSize)
            {
                DragSurface.Width = handleSize;
                DragSurface.Height = handleSize;
                MoveHandleIcon.FontSize = handleSize * 0.62d;
            }
        }

        var differs = AspectLockCheckBox.IsChecked != true
            && AspectRatio.ExceedsTolerance(
                AspectRatio.Calculate(width, height),
                ExpectedAspectRatio);
        AspectWarningPanel.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Sizes the centre move handle to the space the heading and the controls leave free, and
    /// returns zero when that space is too small to draw a legible glyph in.
    /// </summary>
    /// <summary>
    /// Decides whether the description line fits, with a dead band between the two thresholds so
    /// that dragging a corner along the limit cannot flicker it in and out frame after frame.
    /// </summary>
    internal static Visibility GetDescriptionVisibility(
        double width,
        double height,
        Visibility current)
    {
        const double showWidth = 400d;
        const double showHeight = 260d;
        const double hideWidth = 360d;
        const double hideHeight = 240d;

        if (current == Visibility.Visible)
        {
            return width < hideWidth || height < hideHeight
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        return width >= showWidth && height >= showHeight
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal static double GetMoveHandleSize(double width, double freeHeight)
    {
        const double minimumSize = 44d;
        const double maximumSize = 112d;
        const double step = 4d;

        var size = Math.Min(freeHeight - 12d, width * 0.45d);
        if (size < minimumSize)
        {
            return 0d;
        }

        // Quantised so a resize drag does not re-render the glyph on every single frame, and
        // so the handle does not visibly breathe while the window is being sized.
        return Math.Max(minimumSize, Math.Floor(Math.Min(size, maximumSize) / step) * step);
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
            Stroke = annotationBrush,
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
