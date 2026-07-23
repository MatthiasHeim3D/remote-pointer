using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Overlays;

public partial class TargetRegionWindow : Window
{
    private const int RippleDurationMilliseconds = 500;

    private readonly RectangleD resetRectangle;
    private bool isPointingMode;

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

    private void OnResizeThumbDragDelta(
        object sender,
        System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (isPointingMode)
        {
            return;
        }

        var resized = TargetRegionGeometry.Resize(
            ActualWidth > 0d ? ActualWidth : Width,
            ActualHeight > 0d ? ActualHeight : Height,
            e.HorizontalChange,
            e.VerticalChange,
            ExpectedAspectRatio,
            AspectLockCheckBox.IsChecked == true);
        Width = resized.X;
        Height = resized.Y;
        UpdateMetrics();
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

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CalibrationCancelled?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (isPointingMode && e.Key == Key.Escape)
        {
            e.Handled = true;
            PointingExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!isPointingMode)
        {
            return;
        }

        e.Handled = true;
        var click = e.GetPosition(TargetSurface);
        var width = Math.Max(1d, TargetSurface.ActualWidth);
        var height = Math.Max(1d, TargetSurface.ActualHeight);
        var point = CoordinateMapper.Normalize(
            new PointD(click.X, click.Y),
            new RectangleD(0d, 0d, width, height));

        ShowRipple(click);
        PointerCaptured?.Invoke(this, new PointerCapturedEventArgs(point));
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

    private void ShowRipple(Point click)
    {
        const double diameter = 54d;
        var ring = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 92, 92)),
            StrokeThickness = 4d,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5d, 0.5d),
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
