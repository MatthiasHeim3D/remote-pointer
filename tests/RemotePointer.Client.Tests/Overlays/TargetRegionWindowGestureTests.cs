using System.Windows.Input;
using RemotePointer.Client.Overlays;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Overlays;

public sealed class TargetRegionWindowGestureTests
{
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
}
