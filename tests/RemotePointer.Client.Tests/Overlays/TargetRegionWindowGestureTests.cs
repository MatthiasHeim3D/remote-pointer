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
    [InlineData(1_920d, 800d, 112d)]
    [InlineData(640d, 200d, 112d)]
    [InlineData(500d, 90d, 78d)]
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
