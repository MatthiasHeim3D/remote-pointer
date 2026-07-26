using System.Windows;
using System.Windows.Input;
using RemotePointer.Client.Overlays;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Overlays;

public sealed class TargetRegionWindowGestureTests
{
    [Theory]
    [InlineData(false, true, Visibility.Collapsed, Visibility.Collapsed)]
    [InlineData(false, false, Visibility.Visible, Visibility.Collapsed)]
    [InlineData(true, true, Visibility.Collapsed, Visibility.Visible)]
    [InlineData(true, false, Visibility.Visible, Visibility.Collapsed)]
    public void GetUsageHintVisibilities_AlwaysAllowsExpandedHelp(
        bool showCollapsedHint,
        bool isCollapsed,
        Visibility expectedHelp,
        Visibility expectedCollapsedHint)
    {
        var (help, collapsedHint) = TargetRegionWindow.GetUsageHintVisibilities(
            showCollapsedHint,
            isCollapsed);

        Assert.Equal(expectedHelp, help);
        Assert.Equal(expectedCollapsedHint, collapsedHint);
    }

    [Theory]
    [InlineData(true, false, PointerKind.CircleStart)]
    [InlineData(false, false, PointerKind.CircleUpdate)]
    [InlineData(false, true, PointerKind.CircleEnd)]
    public void GetGestureKind_MapsShiftRightDragToCircleLifecycle(
        bool start,
        bool end,
        PointerKind expected)
    {
        var kind = TargetRegionWindow.GetGestureKind(
            MouseButton.Right,
            withShift: true,
            start,
            end);

        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(1.0d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(1.75d)]
    public void GetDevicePlacement_PinsTheEdgeOppositeATopLeftDrag(double scale)
    {
        // A top-left drag holds the right and bottom edges still and walks the origin, exactly
        // as the resize does frame by frame.
        const double anchorRight = 1_313.4d;
        const double anchorBottom = 807.7d;

        var placements = Enumerable
            .Range(0, 60)
            .Select(step =>
            {
                var left = 300.4d - (step * 1.3d);
                var top = 200.9d - (step * 0.7d);
                return TargetRegionWindow.GetDevicePlacement(
                    new RemotePointer.Contracts.Coordinates.RectangleD(
                        left,
                        top,
                        anchorRight - left,
                        anchorBottom - top),
                    scale,
                    scale);
            })
            .ToArray();

        Assert.Single(placements.Select(placement => placement.Right).Distinct());
        Assert.Single(placements.Select(placement => placement.Bottom).Distinct());
    }

    [Theory]
    [InlineData(900d, 600d, Visibility.Collapsed, Visibility.Visible)]
    [InlineData(900d, 600d, Visibility.Visible, Visibility.Visible)]
    [InlineData(300d, 600d, Visibility.Visible, Visibility.Collapsed)]
    [InlineData(300d, 600d, Visibility.Collapsed, Visibility.Collapsed)]
    // Inside the dead band the current state wins, so a drag along the limit cannot flicker.
    [InlineData(380d, 250d, Visibility.Visible, Visibility.Visible)]
    [InlineData(380d, 250d, Visibility.Collapsed, Visibility.Collapsed)]
    public void GetDescriptionVisibility_HoldsStateInsideTheDeadBand(
        double width,
        double height,
        Visibility current,
        Visibility expected)
    {
        var visibility = TargetRegionWindow.GetDescriptionVisibility(width, height, current);

        Assert.Equal(expected, visibility);
    }

    [Theory]
    [InlineData(1_920d, 800d, 112d)]
    [InlineData(640d, 200d, 112d)]
    [InlineData(500d, 90d, 76d)]
    [InlineData(160d, 400d, 72d)]
    [InlineData(640d, 50d, 0d)]
    [InlineData(80d, 400d, 0d)]
    public void GetMoveHandleSize_FitsTheFreeSpaceOrDisappears(
        double width,
        double freeHeight,
        double expected)
    {
        var size = TargetRegionWindow.GetMoveHandleSize(width, freeHeight);

        Assert.Equal(expected, size, precision: 12);
    }
}
