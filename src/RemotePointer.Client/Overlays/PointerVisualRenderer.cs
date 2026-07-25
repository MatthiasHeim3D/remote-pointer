using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Overlays;

internal sealed class PointerVisualRenderer
{
    private const int GestureFailSafeMilliseconds = 3_000;
    private const int GestureLeaseCheckMilliseconds = 250;
    private const int GestureHoldMilliseconds = 350;
    private const int GestureFadeMilliseconds = 450;
    private const int TextHoldMilliseconds = 2_500;
    private const int TextFadeMilliseconds = 650;
    private const int MaximumPathPoints = 2_048;
    private const int MaximumTransientVisuals = 20;

    // Each sender draws one gesture at a time and a receiver accepts at most sixteen senders,
    // so this only bites when a sender opens gestures it never ends.
    private const int MaximumActiveGestures = 16;

    private static readonly Brush AccentBrush =
        new SolidColorBrush(Color.FromRgb(255, 92, 92));
    private static readonly Brush AccentFillBrush =
        new SolidColorBrush(Color.FromArgb(38, 255, 92, 92));

    private readonly Canvas canvas;
    private readonly Dictionary<Guid, ActiveGesture> activeGestures = [];
    private readonly LinkedList<FrameworkElement> transientVisuals = [];
    private readonly DispatcherTimer gestureLeaseTimer;

    public PointerVisualRenderer(Canvas canvas)
    {
        this.canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        gestureLeaseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(GestureLeaseCheckMilliseconds),
        };
        gestureLeaseTimer.Tick += OnGestureLeaseTimerTick;
    }

    public void Show(
        PointerKind kind,
        Point point,
        Guid? gestureId,
        string? text,
        IReadOnlyList<Point>? pathPoints = null)
    {
        switch (kind)
        {
            case PointerKind.PathStart:
                StartPath(gestureId, point);
                break;
            case PointerKind.PathUpdate:
                UpdatePath(gestureId, point, pathPoints, end: false);
                break;
            case PointerKind.PathEnd:
                UpdatePath(gestureId, point, pathPoints, end: true);
                break;
            case PointerKind.LineStart:
                StartLine(gestureId, point);
                break;
            case PointerKind.LineUpdate:
                UpdateLine(gestureId, point, end: false);
                break;
            case PointerKind.LineEnd:
                UpdateLine(gestureId, point, end: true);
                break;
            case PointerKind.RectangleStart:
                StartRectangle(gestureId, point);
                break;
            case PointerKind.RectangleUpdate:
                UpdateRectangle(gestureId, point, end: false);
                break;
            case PointerKind.RectangleEnd:
                UpdateRectangle(gestureId, point, end: true);
                break;
            case PointerKind.CircleStart:
                StartCircle(gestureId, point);
                break;
            case PointerKind.CircleUpdate:
                UpdateCircle(gestureId, point, end: false);
                break;
            case PointerKind.CircleEnd:
                UpdateCircle(gestureId, point, end: true);
                break;
            case PointerKind.Text when !string.IsNullOrWhiteSpace(text):
                ShowText(point, text);
                break;
        }
    }

    public void Clear()
    {
        gestureLeaseTimer.Stop();
        activeGestures.Clear();
        transientVisuals.Clear();
        canvas.Children.Clear();
    }

    private void StartPath(Guid? gestureId, Point point)
    {
        if (!TryStartGesture(gestureId, CreatePath(point), point, out var gesture))
        {
            return;
        }

        TouchGesture(gesture);
    }

    private void UpdatePath(
        Guid? gestureId,
        Point point,
        IReadOnlyList<Point>? pathPoints,
        bool end)
    {
        if (!TryGetGesture<Polyline>(gestureId, out var gesture, out var path))
        {
            return;
        }

        if (pathPoints is null)
        {
            AddPathPoint(path, point);
        }
        else
        {
            foreach (var pathPoint in pathPoints)
            {
                AddPathPoint(path, pathPoint);
            }
        }

        CompleteOrTouch(gesture, end);
    }

    private static void AddPathPoint(Polyline path, Point point)
    {
        if (path.Points.Count > 0 && path.Points[^1] == point)
        {
            return;
        }

        if (path.Points.Count >= MaximumPathPoints)
        {
            for (var index = path.Points.Count - 2; index > 0; index -= 2)
            {
                path.Points.RemoveAt(index);
            }
        }

        path.Points.Add(point);
    }

    private void StartLine(Guid? gestureId, Point point)
    {
        var line = new Line
        {
            X1 = point.X,
            Y1 = point.Y,
            X2 = point.X,
            Y2 = point.Y,
            Stroke = AccentBrush,
            StrokeThickness = 5d,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        if (TryStartGesture(gestureId, line, point, out var gesture))
        {
            TouchGesture(gesture);
        }
    }

    private void UpdateLine(Guid? gestureId, Point point, bool end)
    {
        if (!TryGetGesture<Line>(gestureId, out var gesture, out var line))
        {
            return;
        }

        line.X2 = point.X;
        line.Y2 = point.Y;
        CompleteOrTouch(gesture, end);
    }

    private void StartRectangle(Guid? gestureId, Point point)
    {
        var rectangle = new Rectangle
        {
            Stroke = AccentBrush,
            StrokeThickness = 4d,
            Fill = AccentFillBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rectangle, point.X);
        Canvas.SetTop(rectangle, point.Y);
        if (TryStartGesture(gestureId, rectangle, point, out var gesture))
        {
            TouchGesture(gesture);
        }
    }

    private void UpdateRectangle(Guid? gestureId, Point point, bool end)
    {
        if (!TryGetGesture<Rectangle>(gestureId, out var gesture, out var rectangle))
        {
            return;
        }

        Canvas.SetLeft(rectangle, Math.Min(gesture.Start.X, point.X));
        Canvas.SetTop(rectangle, Math.Min(gesture.Start.Y, point.Y));
        rectangle.Width = Math.Abs(point.X - gesture.Start.X);
        rectangle.Height = Math.Abs(point.Y - gesture.Start.Y);
        CompleteOrTouch(gesture, end);
    }

    private void StartCircle(Guid? gestureId, Point point)
    {
        var circle = new Ellipse
        {
            Stroke = AccentBrush,
            StrokeThickness = 4d,
            Fill = AccentFillBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(circle, point.X);
        Canvas.SetTop(circle, point.Y);
        if (TryStartGesture(gestureId, circle, point, out var gesture))
        {
            TouchGesture(gesture);
        }
    }

    private void UpdateCircle(Guid? gestureId, Point point, bool end)
    {
        if (!TryGetGesture<Ellipse>(gestureId, out var gesture, out var circle))
        {
            return;
        }

        var bounds = CalculateCircleBounds(gesture.Start, point);
        Canvas.SetLeft(circle, bounds.Left);
        Canvas.SetTop(circle, bounds.Top);
        circle.Width = bounds.Width;
        circle.Height = bounds.Height;
        CompleteOrTouch(gesture, end);
    }

    internal static Rect CalculateCircleBounds(Point center, Point edge)
    {
        var radius = Math.Sqrt(
            Math.Pow(edge.X - center.X, 2d)
            + Math.Pow(edge.Y - center.Y, 2d));
        var diameter = radius * 2d;
        return new Rect(center.X - radius, center.Y - radius, diameter, diameter);
    }

    private void ShowText(Point point, string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 18d,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320d,
        };
        var border = new Border
        {
            Padding = new Thickness(9d, 6d, 9d, 6d),
            Background = new SolidColorBrush(Color.FromArgb(230, 17, 23, 32)),
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(2d),
            CornerRadius = new CornerRadius(5d),
            Child = textBlock,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(border, point.X + 14d);
        Canvas.SetTop(border, point.Y - 8d);
        AddTransient(border);
        BeginFade(border, TextHoldMilliseconds, TextFadeMilliseconds, () => RemoveTransient(border));
    }

    private bool TryStartGesture(
        Guid? gestureId,
        FrameworkElement element,
        Point start,
        out ActiveGesture gesture)
    {
        if (!gestureId.HasValue || gestureId.Value == Guid.Empty)
        {
            gesture = null!;
            return false;
        }

        if (activeGestures.Remove(gestureId.Value, out var existing))
        {
            canvas.Children.Remove(existing.Element);
        }

        while (activeGestures.Count >= MaximumActiveGestures)
        {
            RemoveOldestGesture();
        }

        gesture = new ActiveGesture(gestureId.Value, element, start);
        activeGestures.Add(gesture.Id, gesture);
        canvas.Children.Add(element);
        return true;
    }

    private bool TryGetGesture<TElement>(
        Guid? gestureId,
        out ActiveGesture gesture,
        out TElement element)
        where TElement : FrameworkElement
    {
        if (gestureId.HasValue
            && activeGestures.TryGetValue(gestureId.Value, out gesture!)
            && gesture.Element is TElement typedElement)
        {
            element = typedElement;
            return true;
        }

        gesture = null!;
        element = null!;
        return false;
    }

    private void RemoveOldestGesture()
    {
        var oldest = activeGestures.Values.MinBy(gesture => gesture.LastTouchedAt);
        if (oldest is null)
        {
            return;
        }

        _ = activeGestures.Remove(oldest.Id);
        canvas.Children.Remove(oldest.Element);
    }

    private void CompleteOrTouch(ActiveGesture gesture, bool end)
    {
        if (!end)
        {
            TouchGesture(gesture);
            return;
        }

        _ = activeGestures.Remove(gesture.Id);
        StopLeaseTimerWhenIdle();
        BeginFade(
            gesture.Element,
            GestureHoldMilliseconds,
            GestureFadeMilliseconds,
            () => canvas.Children.Remove(gesture.Element));
    }

    private void TouchGesture(ActiveGesture gesture)
    {
        gesture.Element.BeginAnimation(UIElement.OpacityProperty, null);
        gesture.Element.Opacity = 1d;
        gesture.LastTouchedAt = Environment.TickCount64;
        if (!gestureLeaseTimer.IsEnabled)
        {
            gestureLeaseTimer.Start();
        }
    }

    private void OnGestureLeaseTimerTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var expired = activeGestures.Values
            .Where(gesture => now - gesture.LastTouchedAt >= GestureFailSafeMilliseconds)
            .ToArray();
        foreach (var gesture in expired)
        {
            if (!activeGestures.Remove(gesture.Id))
            {
                continue;
            }

            BeginFade(
                gesture.Element,
                holdMilliseconds: 0,
                GestureFadeMilliseconds,
                () => canvas.Children.Remove(gesture.Element));
        }

        StopLeaseTimerWhenIdle();
    }

    private void StopLeaseTimerWhenIdle()
    {
        if (activeGestures.Count == 0)
        {
            gestureLeaseTimer.Stop();
        }
    }

    private void AddTransient(FrameworkElement element)
    {
        while (transientVisuals.Count >= MaximumTransientVisuals)
        {
            RemoveTransient(transientVisuals.First!.Value);
        }

        canvas.Children.Add(element);
        transientVisuals.AddLast(element);
    }

    private void RemoveTransient(FrameworkElement element)
    {
        _ = transientVisuals.Remove(element);
        canvas.Children.Remove(element);
    }

    private static Polyline CreatePath(Point point) => new()
    {
        Points = new PointCollection([point]),
        Stroke = AccentBrush,
        StrokeThickness = 5d,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
    };

    private static void BeginFade(
        FrameworkElement element,
        int holdMilliseconds,
        int fadeMilliseconds,
        Action completed)
    {
        var fade = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(fadeMilliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(holdMilliseconds),
        };
        fade.Completed += (_, _) => completed();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private sealed class ActiveGesture(Guid id, FrameworkElement element, Point start)
    {
        public Guid Id { get; } = id;

        public FrameworkElement Element { get; } = element;

        public Point Start { get; } = start;

        public long LastTouchedAt { get; set; }
    }
}
