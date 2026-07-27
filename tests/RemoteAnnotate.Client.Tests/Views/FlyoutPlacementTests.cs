using RemoteAnnotate.Client.Native;
using RemoteAnnotate.Client.Views;

namespace RemoteAnnotate.Client.Tests.Views;

public sealed class FlyoutPlacementTests
{
    private static NativeRectangle Rectangle(int left, int top, int right, int bottom) =>
        new()
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };

    [Fact]
    public void CalculateBottomCorner_SeatsTheWindowInsideTheWorkArea()
    {
        // 1920x1080 with a 40px taskbar.
        var workArea = Rectangle(0, 0, 1_920, 1_040);
        var window = Rectangle(0, 0, 400, 200);

        var (x, y) = FlyoutPlacement.CalculateBottomCorner(workArea, window, dpi: 96d);

        Assert.Equal(1_920 - 400 - 12, x);
        Assert.Equal(1_040 - 200 - 12, y);
    }

    [Theory]
    [InlineData(96d, 12)]
    [InlineData(120d, 15)]
    [InlineData(144d, 18)]
    [InlineData(192d, 24)]
    public void CalculateBottomCorner_ScalesTheEdgeMarginWithTheDisplayScale(
        double dpi,
        int expectedMargin)
    {
        var workArea = Rectangle(0, 0, 1_000, 1_000);
        var window = Rectangle(0, 0, 100, 100);

        var (x, y) = FlyoutPlacement.CalculateBottomCorner(workArea, window, dpi);

        Assert.Equal(1_000 - 100 - expectedMargin, x);
        Assert.Equal(1_000 - 100 - expectedMargin, y);
    }

    [Fact]
    public void CalculateBottomCorner_UsesThePhysicalWindowSizeRatherThanADipSize()
    {
        // The same flyout at 150%: both the work area and the window come back larger, so the
        // corner gap stays proportional instead of drifting by the scale factor.
        var workArea = Rectangle(0, 0, 2_880, 1_560);
        var window = Rectangle(0, 0, 600, 300);

        var (x, y) = FlyoutPlacement.CalculateBottomCorner(workArea, window, dpi: 144d);

        Assert.Equal(2_880 - 600 - 18, x);
        Assert.Equal(1_560 - 300 - 18, y);
    }

    [Fact]
    public void CalculateBottomCorner_KeepsTheOffsetOfAMonitorLeftOfThePrimary()
    {
        var workArea = Rectangle(-1_920, -120, 0, 960);
        var window = Rectangle(0, 0, 400, 200);

        var (x, y) = FlyoutPlacement.CalculateBottomCorner(workArea, window, dpi: 96d);

        Assert.Equal(0 - 400 - 12, x);
        Assert.Equal(960 - 200 - 12, y);
    }

    [Fact]
    public void CalculateBottomCorner_TreatsAFailedDpiQueryAsUnscaled()
    {
        var workArea = Rectangle(0, 0, 1_920, 1_040);
        var window = Rectangle(0, 0, 400, 200);

        var (x, y) = FlyoutPlacement.CalculateBottomCorner(workArea, window, dpi: 0d);

        Assert.Equal(1_920 - 400 - 12, x);
        Assert.Equal(1_040 - 200 - 12, y);
    }
}
