using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Overlays;

internal sealed class PointerVisualRenderer
{
    private const int GestureFailSafeMilliseconds = 3_000;
    private const int GestureLeaseCheckMilliseconds = 250;
    private const int GestureHoldMilliseconds = 350;
    private const int GestureFadeMilliseconds = 450;
    private const int TextHoldMilliseconds = 2_500;
    private const int TextFadeMilliseconds = 650;
    private const int MaximumTransientVisuals = 20;

    // Each annotator draws one gesture at a time and a host accepts at most sixteen annotators,
    // so this only bites when an annotator opens gestures it never ends.
    private const int MaximumActiveGestures = 16;

    // One entry per colour actually seen. Sixteen annotators can pick sixteen colours and each
    // may change its own, so the cache is emptied rather than grown once it stops paying for
    // itself; the next gesture simply builds its brushes again.
    private const int MaximumCachedAccents = 32;

    private readonly Canvas canvas;
    private readonly bool smoothRemoteGestures;
    private Color defaultAccent;
    private readonly Dictionary<Color, AccentBrushes> accentBrushes = [];
    private readonly Dictionary<Guid, ActiveGesture> activeGestures = [];
    private readonly List<ActiveGesture> arrivedGestures = [];
    private readonly LinkedList<FrameworkElement> transientVisuals = [];
    private readonly DispatcherTimer gestureLeaseTimer;
    private bool isFrameLoopHooked;
    private bool hasFrameBaseline;
    private TimeSpan lastFrameTime;

    /// <summary>
    /// Creates a renderer over <paramref name="canvas"/>.
    /// </summary>
    /// <param name="canvas">The surface the gestures are drawn on.</param>
    /// <param name="smoothRemoteGestures">
    /// Whether arriving points are paced onto the render loop instead of being drawn the
    /// moment they land. The host overlay turns this on, because everything it draws crossed
    /// the relay and arrives in irregular clumps. The annotator's own target area leaves it
    /// off: those points are the local mouse, and smoothing would only put lag between the
    /// hand and the ink it is aiming with.
    /// </param>
    /// <param name="defaultAccent">
    /// The colour used for gestures that name none. The annotator's target area sets its own
    /// configured colour here and never overrides it per gesture; the host leaves it at the
    /// default and colours each gesture from the event that opened it, so two annotators drawing
    /// at once stay told apart.
    /// </param>
    public PointerVisualRenderer(
        Canvas canvas,
        bool smoothRemoteGestures = false,
        Color? defaultAccent = null)
    {
        this.canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        this.smoothRemoteGestures = smoothRemoteGestures;
        this.defaultAccent = defaultAccent ?? AnnotationPalette.DefaultAccent;
        gestureLeaseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(GestureLeaseCheckMilliseconds),
        };
        gestureLeaseTimer.Tick += OnGestureLeaseTimerTick;
    }

    /// <summary>
    /// Draws one arriving pointer event.
    /// </summary>
    /// <param name="accent">
    /// The colour the annotator drew in, honoured only by the events that create a visual: a
    /// gesture keeps the colour it opened with for its whole life, so a colour changed mid-stroke
    /// cannot repaint what is already on the canvas.
    /// </param>
    public void Show(
        PointerKind kind,
        Point point,
        Guid? gestureId,
        string? text,
        IReadOnlyList<Point>? pathPoints = null,
        Color? accent = null)
    {
        switch (kind)
        {
            case PointerKind.PathStart:
                StartPath(gestureId, point, accent);
                break;
            case PointerKind.PathUpdate:
                UpdatePath(gestureId, point, pathPoints, end: false);
                break;
            case PointerKind.PathEnd:
                UpdatePath(gestureId, point, pathPoints, end: true);
                break;
            case PointerKind.LineStart:
                StartLine(gestureId, point, accent);
                break;
            case PointerKind.LineUpdate:
                UpdateLine(gestureId, point, end: false);
                break;
            case PointerKind.LineEnd:
                UpdateLine(gestureId, point, end: true);
                break;
            case PointerKind.RectangleStart:
                StartRectangle(gestureId, point, accent);
                break;
            case PointerKind.RectangleUpdate:
                UpdateRectangle(gestureId, point, end: false);
                break;
            case PointerKind.RectangleEnd:
                UpdateRectangle(gestureId, point, end: true);
                break;
            case PointerKind.CircleStart:
                StartCircle(gestureId, point, accent);
                break;
            case PointerKind.CircleUpdate:
                UpdateCircle(gestureId, point, end: false);
                break;
            case PointerKind.CircleEnd:
                UpdateCircle(gestureId, point, end: true);
                break;
            case PointerKind.Text when !string.IsNullOrWhiteSpace(text):
                ShowText(point, text, accent);
                break;
        }
    }

    /// <summary>
    /// Changes the colour used for gestures that name none. Only gestures started afterwards
    /// pick it up: whatever is already on the canvas holds the brush it was created with, so a
    /// colour changed mid-stroke cannot rewrite ink the annotator has already drawn.
    /// </summary>
    public void SetDefaultAccent(Color accent) => defaultAccent = accent;

    public void Clear()
    {
        gestureLeaseTimer.Stop();
        StopFrameLoop();
        arrivedGestures.Clear();
        activeGestures.Clear();
        transientVisuals.Clear();
        canvas.Children.Clear();
    }

    /// <summary>
    /// The stroke and fill brushes for one accent, built once and reused. Every gesture start
    /// would otherwise allocate a pair, and a freehand stroke starts as often as the hand moves
    /// between shapes.
    /// </summary>
    private AccentBrushes GetAccentBrushes(Color? accent)
    {
        var color = accent ?? defaultAccent;
        if (accentBrushes.TryGetValue(color, out var brushes))
        {
            return brushes;
        }

        if (accentBrushes.Count >= MaximumCachedAccents)
        {
            accentBrushes.Clear();
        }

        brushes = new AccentBrushes(
            AnnotationPalette.CreateStrokeBrush(color),
            AnnotationPalette.CreateFillBrush(color));
        accentBrushes.Add(color, brushes);
        return brushes;
    }

    private void StartPath(Guid? gestureId, Point point, Color? accent)
    {
        var path = CreatePath(point, GetAccentBrushes(accent).Stroke);
        if (!TryStartGesture(gestureId, path, point, out var gesture))
        {
            return;
        }

        gesture.PathBuilder = new FreehandPathBuilder(point, smoothRemoteGestures);
        if (smoothRemoteGestures)
        {
            gesture.PendingPathPoints = new Queue<Point>();
        }

        TouchGesture(gesture);
    }

    private void UpdatePath(
        Guid? gestureId,
        Point point,
        IReadOnlyList<Point>? pathPoints,
        bool end)
    {
        if (!TryGetGesture<Polyline>(gestureId, out var gesture, out var path)
            || gesture.PathBuilder is not { } builder)
        {
            return;
        }

        if (gesture.PendingPathPoints is not { } pending)
        {
            if (pathPoints is null)
            {
                builder.Append(point, path.Points);
            }
            else
            {
                foreach (var pathPoint in pathPoints)
                {
                    builder.Append(pathPoint, path.Points);
                }
            }

            CompleteOrTouch(gesture, end);
            return;
        }

        // The whole batch is queued rather than drawn, so the render loop can let the tip
        // advance at the speed it was drawn at instead of in one step per arriving message.
        if (pathPoints is null)
        {
            pending.Enqueue(point);
        }
        else
        {
            foreach (var pathPoint in pathPoints)
            {
                pending.Enqueue(pathPoint);
            }
        }

        AnimateOrClose(gesture, end);
    }

    private void StartLine(Guid? gestureId, Point point, Color? accent)
    {
        var line = new Line
        {
            X1 = point.X,
            Y1 = point.Y,
            X2 = point.X,
            Y2 = point.Y,
            Stroke = GetAccentBrushes(accent).Stroke,
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
        if (TryGetGesture<Line>(gestureId, out var gesture, out _))
        {
            ReceiveMotion(gesture, point, end);
        }
    }

    private void StartRectangle(Guid? gestureId, Point point, Color? accent)
    {
        var brushes = GetAccentBrushes(accent);
        var rectangle = new Rectangle
        {
            Stroke = brushes.Stroke,
            StrokeThickness = 4d,
            Fill = brushes.Fill,
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
        if (TryGetGesture<Rectangle>(gestureId, out var gesture, out _))
        {
            ReceiveMotion(gesture, point, end);
        }
    }

    private void StartCircle(Guid? gestureId, Point point, Color? accent)
    {
        var brushes = GetAccentBrushes(accent);
        var circle = new Ellipse
        {
            Stroke = brushes.Stroke,
            StrokeThickness = 4d,
            Fill = brushes.Fill,
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
        if (TryGetGesture<Ellipse>(gestureId, out var gesture, out _))
        {
            ReceiveMotion(gesture, point, end);
        }
    }

    internal static Rect CalculateCircleBounds(Point center, Point edge)
    {
        var radius = Math.Sqrt(
            Math.Pow(edge.X - center.X, 2d)
            + Math.Pow(edge.Y - center.Y, 2d));
        var diameter = radius * 2d;
        return new Rect(center.X - radius, center.Y - radius, diameter, diameter);
    }

    /// <summary>
    /// Records the point a dragged shape was last sent. Without smoothing the shape follows
    /// it immediately; with smoothing the render loop walks the shape there instead.
    /// </summary>
    private void ReceiveMotion(ActiveGesture gesture, Point point, bool end)
    {
        gesture.Target = point;
        if (smoothRemoteGestures)
        {
            AnimateOrClose(gesture, end);
            return;
        }

        gesture.Displayed = point;
        ApplyMotion(gesture);
        CompleteOrTouch(gesture, end);
    }

    /// <summary>
    /// Hands a smoothed gesture to the render loop, or marks it released so the loop runs it
    /// out to its final point before the fade starts.
    /// </summary>
    private void AnimateOrClose(ActiveGesture gesture, bool end)
    {
        TouchGesture(gesture);
        if (end)
        {
            gesture.IsClosing = true;
            gesture.ClosingSince = Environment.TickCount64;
        }

        EnsureFrameLoop();
    }

    /// <summary>
    /// Draws a dragged shape at the point it has currently reached.
    /// </summary>
    private static void ApplyMotion(ActiveGesture gesture)
    {
        var point = gesture.Displayed;
        switch (gesture.Element)
        {
            case Line line:
                line.X2 = point.X;
                line.Y2 = point.Y;
                break;
            case Rectangle rectangle:
                Canvas.SetLeft(rectangle, Math.Min(gesture.Start.X, point.X));
                Canvas.SetTop(rectangle, Math.Min(gesture.Start.Y, point.Y));
                rectangle.Width = Math.Abs(point.X - gesture.Start.X);
                rectangle.Height = Math.Abs(point.Y - gesture.Start.Y);
                break;
            case Ellipse circle:
                var bounds = CalculateCircleBounds(gesture.Start, point);
                Canvas.SetLeft(circle, bounds.Left);
                Canvas.SetTop(circle, bounds.Top);
                circle.Width = bounds.Width;
                circle.Height = bounds.Height;
                break;
        }
    }

    private void EnsureFrameLoop()
    {
        if (!smoothRemoteGestures || isFrameLoopHooked)
        {
            return;
        }

        isFrameLoopHooked = true;
        hasFrameBaseline = false;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopFrameLoop()
    {
        if (!isFrameLoopHooked)
        {
            return;
        }

        isFrameLoopHooked = false;
        hasFrameBaseline = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void StopFrameLoopWhenIdle()
    {
        if (activeGestures.Count == 0)
        {
            StopFrameLoop();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!TryGetFrameInterval(e, out var elapsedMilliseconds))
        {
            return;
        }

        foreach (var gesture in activeGestures.Values)
        {
            if (AdvanceGesture(gesture, elapsedMilliseconds))
            {
                arrivedGestures.Add(gesture);
            }
        }

        foreach (var gesture in arrivedGestures)
        {
            CompleteGesture(gesture, GestureHoldMilliseconds);
        }

        arrivedGestures.Clear();
        StopFrameLoopWhenIdle();
    }

    /// <summary>
    /// Measures how long the last displayed frame lasted. WPF can raise
    /// <see cref="CompositionTarget.Rendering"/> more than once for the same frame, and the
    /// first raise after hooking has nothing to measure against; both report no elapsed time
    /// rather than a zero-length step that would move everything nowhere.
    /// </summary>
    private bool TryGetFrameInterval(EventArgs e, out double elapsedMilliseconds)
    {
        elapsedMilliseconds = 0d;
        if (e is not RenderingEventArgs rendering)
        {
            return false;
        }

        var frameTime = rendering.RenderingTime;
        if (hasFrameBaseline && frameTime > lastFrameTime)
        {
            elapsedMilliseconds = (frameTime - lastFrameTime).TotalMilliseconds;
        }

        hasFrameBaseline = true;
        lastFrameTime = frameTime;
        return elapsedMilliseconds > 0d;
    }

    /// <summary>
    /// Moves one gesture on by a frame and reports whether a released gesture has finished
    /// arriving, so the caller can start its fade.
    /// </summary>
    private static bool AdvanceGesture(ActiveGesture gesture, double elapsedMilliseconds)
    {
        if (gesture.Element is Polyline path)
        {
            return AdvancePath(gesture, path, elapsedMilliseconds);
        }

        gesture.Displayed = GestureMotion.Advance(
            gesture.Displayed,
            gesture.Target,
            elapsedMilliseconds,
            GestureMotion.MotionTimeConstantMilliseconds);
        ApplyMotion(gesture);
        return gesture.IsClosing
            && (GestureMotion.HasArrived(gesture.Displayed, gesture.Target)
                || gesture.HasReachedSettleDeadline);
    }

    private static bool AdvancePath(
        ActiveGesture gesture,
        Polyline path,
        double elapsedMilliseconds)
    {
        if (gesture.PendingPathPoints is not { } pending || gesture.PathBuilder is not { } builder)
        {
            return gesture.IsClosing;
        }

        var releaseCount = GestureMotion.PathPointsToRelease(
            pending.Count,
            elapsedMilliseconds,
            GestureMotion.PathReleaseTimeConstantMilliseconds);
        for (var index = 0; index < releaseCount; index++)
        {
            builder.Append(pending.Dequeue(), path.Points);
        }

        return gesture.IsClosing
            && (pending.Count == 0 || gesture.HasReachedSettleDeadline);
    }

    private void ShowText(Point point, string text, Color? accent)
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
            BorderBrush = GetAccentBrushes(accent).Stroke,
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
        if (end)
        {
            CompleteGesture(gesture, GestureHoldMilliseconds);
            return;
        }

        TouchGesture(gesture);
    }

    private void CompleteGesture(ActiveGesture gesture, int holdMilliseconds)
    {
        if (!activeGestures.Remove(gesture.Id))
        {
            return;
        }

        FinishGestureGeometry(gesture);
        StopLeaseTimerWhenIdle();
        StopFrameLoopWhenIdle();
        BeginFade(
            gesture.Element,
            holdMilliseconds,
            GestureFadeMilliseconds,
            () => canvas.Children.Remove(gesture.Element));
    }

    /// <summary>
    /// Puts the gesture exactly where the annotator left it. Smoothing decides how an
    /// annotation reaches its place and never where it ends up, so whatever the render loop
    /// had got to is replaced here by everything that actually arrived.
    /// </summary>
    private static void FinishGestureGeometry(ActiveGesture gesture)
    {
        if (gesture.Element is Polyline path)
        {
            var pending = gesture.PendingPathPoints;
            var builder = gesture.PathBuilder;
            while (builder is not null && pending is { Count: > 0 })
            {
                builder.Append(pending.Dequeue(), path.Points);
            }

            return;
        }

        gesture.Displayed = gesture.Target;
        ApplyMotion(gesture);
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
            CompleteGesture(gesture, holdMilliseconds: 0);
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

    private static Polyline CreatePath(Point point, Brush stroke) => new()
    {
        Points = new PointCollection([point]),
        Stroke = stroke,
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

    private sealed record AccentBrushes(Brush Stroke, Brush Fill);

    private sealed class ActiveGesture(Guid id, FrameworkElement element, Point start)
    {
        public Guid Id { get; } = id;

        public FrameworkElement Element { get; } = element;

        public Point Start { get; } = start;

        /// <summary>The most recent point this gesture was sent.</summary>
        public Point Target { get; set; } = start;

        /// <summary>Where the shape is drawn now, which chases <see cref="Target"/>.</summary>
        public Point Displayed { get; set; } = start;

        /// <summary>Set for freehand gestures, which append points instead of moving one.</summary>
        public FreehandPathBuilder? PathBuilder { get; set; }

        /// <summary>Freehand samples waiting to be released onto the line, when smoothing.</summary>
        public Queue<Point>? PendingPathPoints { get; set; }

        /// <summary>Whether the annotator has released this gesture and it is running out.</summary>
        public bool IsClosing { get; set; }

        public long ClosingSince { get; set; }

        public long LastTouchedAt { get; set; }

        public bool HasReachedSettleDeadline =>
            Environment.TickCount64 - ClosingSince >= GestureMotion.MaximumSettleMilliseconds;
    }
}
