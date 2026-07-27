using RemoteAnnotate.Contracts.Coordinates;

namespace RemoteAnnotate.Client.Services;

public sealed class CalibrationLockedEventArgs(RectangleD rectangle) : EventArgs
{
    public RectangleD Rectangle { get; } = rectangle;
}
