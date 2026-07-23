using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Services;

public sealed class CalibrationLockedEventArgs(RectangleD rectangle) : EventArgs
{
    public RectangleD Rectangle { get; } = rectangle;
}
