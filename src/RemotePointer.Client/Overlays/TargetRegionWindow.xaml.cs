using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RemotePointer.Client.Native;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.Overlays;

public partial class TargetRegionWindow : Window
{
    private const int RippleDurationMilliseconds = 500;
    private const int GestureUpdateIntervalMilliseconds = 50;

    private readonly RectangleD resetRectangle;
    private readonly PointerVisualRenderer pointerVisuals;
    private TextBox? activeTextEditor;
    private Point activeTextPosition;
    private MouseButton? activePointerButton;
    private Point pointerDownPosition;
    private bool pointerDownWithShift;
    private bool isPointerGestureActive;
    private Guid activeGestureId;
    private long lastGestureUpdateAt;
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
        bool lockAspectRatio)
    {
        if (!double.IsFinite(expectedAspectRatio) || expectedAspectRatio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAspectRatio));
        }

        this.resetRectangle = resetRectangle;
        ExpectedAspectRatio = expectedAspectRatio;

        InitializeComponent();
        pointerVisuals = new PointerVisualRenderer(RippleCanvas);
        ApplyRectangle(rectangle);
        AspectLockCheckBox.IsChecked = lockAspectRatio;
        ExpectedAspectText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Expected receiver aspect ratio: {ExpectedAspectRatio:0.###}:1");
        Loaded += (_, _) => UpdateMetrics();
        SizeChanged += (_, _) => UpdateMetrics();
    }

    public event EventHandler<CalibrationLockedEventArgs>? CalibrationLocked;

    public event EventHandler? CalibrationCancelled;

    public event EventHandler? PointingExitRequested;

    public event EventHandler<PointerCapturedEventArgs>? PointerCaptured;

    public double ExpectedAspectRatio { get; }

    public void EnterPointingMode()
    {
        isPointingMode = true;
        CalibrationPanel.Visibility = Visibility.Collapsed;
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
        if (!IsInitialized || isPointingMode || AspectLockCheckBox.IsChecked != true)
        {
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
        var differs = AspectRatio.ExceedsTolerance(
            AspectRatio.Calculate(width, height),
            ExpectedAspectRatio);

        if (differs && AllowOverrideCheckBox.IsChecked != true)
        {
            AspectWarningText.Text = "The target differs by more than 2%. Enable Allow mismatch to override.";
            AspectWarningText.Visibility = Visibility.Visible;
            AllowOverrideCheckBox.Visibility = Visibility.Visible;
            return;
        }

        CalibrationLocked?.Invoke(
            this,
            new CalibrationLockedEventArgs(new RectangleD(Left, Top, width, height)));
        e.Handled = true;
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        ApplyRectangle(resetRectangle);
        AllowOverrideCheckBox.IsChecked = false;
        UpdateMetrics();
        e.Handled = true;
    }

    private void OnFullscreenClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentMonitorBounds(out var monitorBounds))
        {
            AspectWarningText.Text = "The current monitor bounds could not be determined.";
            AspectWarningText.Foreground = new SolidColorBrush(Color.FromRgb(255, 157, 157));
            AspectWarningText.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }

        ApplyRectangle(TargetRegionGeometry.FitWithin(
            monitorBounds,
            ExpectedAspectRatio,
            AspectLockCheckBox.IsChecked == true));
        AllowOverrideCheckBox.IsChecked = false;
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
        pointerDownWithShift = e.ChangedButton == MouseButton.Left
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        isPointerGestureActive = false;
        activeGestureId = Guid.Empty;
        lastGestureUpdateAt = 0;
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
            lastGestureUpdateAt = 0;
        }

        var updateKind = GetGestureKind(start: false, end: false);
        pointerVisuals.Show(updateKind, current, activeGestureId, text: null);

        var now = Environment.TickCount64;
        if (now - lastGestureUpdateAt >= GestureUpdateIntervalMilliseconds)
        {
            RaisePointerCaptured(current, updateKind, activeGestureId);
            lastGestureUpdateAt = now;
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

        if (isPointerGestureActive)
        {
            var endKind = GetGestureKind(start: false, end: true);
            pointerVisuals.Show(endKind, releasePosition, activeGestureId, text: null);
            RaisePointerCaptured(releasePosition, endKind, activeGestureId);
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
    }

    private PointerKind GetGestureKind(bool start, bool end)
    {
        if (activePointerButton == MouseButton.Right)
        {
            return start
                ? PointerKind.RectangleStart
                : end ? PointerKind.RectangleEnd : PointerKind.RectangleUpdate;
        }

        if (pointerDownWithShift)
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
        string? text = null)
    {
        var width = Math.Max(1d, TargetSurface.ActualWidth);
        var height = Math.Max(1d, TargetSurface.ActualHeight);
        var point = CoordinateMapper.Normalize(
            new PointD(position.X, position.Y),
            new RectangleD(0d, 0d, width, height));

        PointerCaptured?.Invoke(this, new PointerCapturedEventArgs(point, kind, gestureId, text));
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

        var ratio = AspectRatio.Calculate(width, height);
        var difference = TargetRegionGeometry.DifferenceFromExpected(width, height, ExpectedAspectRatio);
        DimensionsText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{width:0} × {height:0} DIPs  •  {ratio:0.###}:1  •  difference {difference:P1}");

        var differs = AspectRatio.ExceedsTolerance(ratio, ExpectedAspectRatio);
        AspectWarningText.Text = differs
            ? "Aspect ratio differs from the receiver by more than 2%."
            : "Aspect ratio is within the 2% tolerance.";
        AspectWarningText.Foreground = differs
            ? new SolidColorBrush(Color.FromRgb(255, 157, 157))
            : new SolidColorBrush(Color.FromRgb(143, 220, 181));
        AspectWarningText.Visibility = Visibility.Visible;
        AllowOverrideCheckBox.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        if (!differs)
        {
            AllowOverrideCheckBox.IsChecked = false;
        }
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
