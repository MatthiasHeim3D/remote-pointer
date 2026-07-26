using System.Windows;

namespace RemotePointer.Client.Overlays;

/// <summary>
/// The arithmetic behind the host's smoothing of gestures that arrived over the relay. An
/// annotator samples its mouse continuously, but the host receives those samples in whatever
/// clumps the network hands over, so drawing each batch the moment it lands makes a drag
/// advance in visible steps. These helpers spread that motion back over the render loop.
/// They hold no state and touch no elements, so one frame can be reasoned about — and
/// tested — on its own.
/// </summary>
internal static class GestureMotion
{
    /// <summary>
    /// How long a dragged shape takes to cover about 63% of the distance to the newest point
    /// it was sent. Short enough that the shape still tracks the annotator's hand, long
    /// enough to absorb a frame or two of arrival jitter.
    /// </summary>
    internal const double MotionTimeConstantMilliseconds = 35d;

    /// <summary>
    /// The same idea for a freehand line, whose samples are appended rather than moved: a
    /// batch that lands in a single frame is released over roughly this long instead of all
    /// at once, so the tip advances at the speed it was drawn at.
    /// </summary>
    internal const double PathReleaseTimeConstantMilliseconds = 30d;

    /// <summary>
    /// How close a shape has to be to its target before a released gesture counts as
    /// arrived. Far below one pixel, because this only decides when the chase stops — the
    /// final position is always assigned exactly rather than approached.
    /// </summary>
    internal const double ArrivalToleranceDips = 0.1d;

    /// <summary>
    /// A released gesture stops chasing after this long however far it still had to travel,
    /// so a placed annotation cannot sit short of where it belongs while it fades.
    /// </summary>
    internal const long MaximumSettleMilliseconds = 220L;

    private const double NeighbourWeight = 0.25d;
    private const double CentreWeight = 0.5d;

    /// <summary>
    /// Moves <paramref name="current"/> part of the way to <paramref name="target"/>. The
    /// fraction covered depends only on how much time actually passed, so a dropped frame
    /// catches up instead of stalling and the result never overshoots.
    /// </summary>
    internal static Point Advance(
        Point current,
        Point target,
        double elapsedMilliseconds,
        double timeConstantMilliseconds)
    {
        if (timeConstantMilliseconds <= 0d)
        {
            return target;
        }

        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds <= 0d)
        {
            return current;
        }

        var progress = 1d - Math.Exp(-elapsedMilliseconds / timeConstantMilliseconds);
        return new Point(
            current.X + ((target.X - current.X) * progress),
            current.Y + ((target.Y - current.Y) * progress));
    }

    /// <summary>
    /// Whether a shape is close enough to the last point it was sent that continuing to
    /// animate it would not be visible.
    /// </summary>
    internal static bool HasArrived(Point current, Point target) =>
        Math.Abs(target.X - current.X) <= ArrivalToleranceDips
        && Math.Abs(target.Y - current.Y) <= ArrivalToleranceDips;

    /// <summary>
    /// How many queued freehand samples to draw this frame. Releasing a fixed share of the
    /// backlog settles at whatever rate samples actually arrive, so the queue neither grows
    /// without bound nor empties into a stall, and a burst is spread over a few frames
    /// rather than jumping the line forward.
    /// </summary>
    internal static int PathPointsToRelease(
        int queuedCount,
        double elapsedMilliseconds,
        double timeConstantMilliseconds)
    {
        if (queuedCount <= 0)
        {
            return 0;
        }

        if (timeConstantMilliseconds <= 0d)
        {
            return queuedCount;
        }

        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds <= 0d)
        {
            return 0;
        }

        var share = queuedCount * (elapsedMilliseconds / timeConstantMilliseconds);
        return Math.Clamp((int)Math.Ceiling(share), 1, queuedCount);
    }

    /// <summary>
    /// Pulls a freehand sample a quarter of the way toward the midpoint of the samples either
    /// side of it. One pass takes the hand tremor and coordinate rounding out of a drawn line
    /// while leaving its shape — and any corner the annotator meant — recognisably where it
    /// was drawn.
    /// </summary>
    internal static Point Smooth(Point previous, Point current, Point next) => new(
        (previous.X * NeighbourWeight) + (current.X * CentreWeight) + (next.X * NeighbourWeight),
        (previous.Y * NeighbourWeight) + (current.Y * CentreWeight) + (next.Y * NeighbourWeight));
}
