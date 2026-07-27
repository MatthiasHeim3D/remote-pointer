using System.Windows;
using RemoteAnnotate.Client.Overlays;

namespace RemoteAnnotate.Client.Tests.Overlays;

public sealed class PointerVisualRendererTests
{
    [Fact]
    public void CalculateCircleBounds_CentersCircleAndUsesPointerDistanceAsRadius()
    {
        var bounds = PointerVisualRenderer.CalculateCircleBounds(
            new Point(100d, 80d),
            new Point(103d, 84d));

        Assert.Equal(95d, bounds.Left);
        Assert.Equal(75d, bounds.Top);
        Assert.Equal(10d, bounds.Width);
        Assert.Equal(10d, bounds.Height);
    }
}
