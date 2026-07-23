using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Services;

public sealed class PointerCapturedEventArgs(NormalizedPoint point) : EventArgs
{
    public NormalizedPoint Point { get; } = point;
}
