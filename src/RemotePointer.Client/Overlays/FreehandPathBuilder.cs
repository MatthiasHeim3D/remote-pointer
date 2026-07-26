using System.Windows;

namespace RemotePointer.Client.Overlays;

/// <summary>
/// Builds the point list of one freehand line as its samples arrive.
/// </summary>
/// <param name="start">The point the line was opened at, already in the list.</param>
/// <param name="smooth">
/// Whether to smooth each sample against its neighbours. The host turns this on because the
/// samples it draws crossed the relay and read as slightly jagged; the annotator's own target
/// area leaves it off, since there the samples are the local mouse and the raw line is what
/// the annotator is aiming with.
/// </param>
internal sealed class FreehandPathBuilder(Point start, bool smooth)
{
    /// <summary>
    /// An upper bound on how many points one line keeps, reached only by a very long gesture.
    /// </summary>
    internal const int MaximumPoints = 2_048;

    private Point lastPoint = start;
    private Point? previousPoint;

    /// <summary>
    /// Adds <paramref name="point"/> to <paramref name="points"/>. When smoothing, the point
    /// that is currently last is first replaced by its smoothed value, which is only knowable
    /// now that its successor has arrived. The newest point is never moved, so a released
    /// line ends exactly on the annotator's release position.
    /// </summary>
    public void Append(Point point, IList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (lastPoint == point)
        {
            return;
        }

        if (smooth && previousPoint is { } previous && points.Count >= 2)
        {
            points[points.Count - 1] = GestureMotion.Smooth(previous, lastPoint, point);
        }

        previousPoint = lastPoint;
        lastPoint = point;
        Thin(points);
        points.Add(point);
    }

    /// <summary>
    /// Halves a line that has reached its cap by dropping every other interior point. Both
    /// ends and the overall shape survive; only the fine detail thins out.
    /// </summary>
    private static void Thin(IList<Point> points)
    {
        if (points.Count < MaximumPoints)
        {
            return;
        }

        for (var index = points.Count - 2; index > 0; index -= 2)
        {
            points.RemoveAt(index);
        }
    }
}
