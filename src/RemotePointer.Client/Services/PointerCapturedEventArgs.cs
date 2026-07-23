using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public sealed class PointerCapturedEventArgs(
    NormalizedPoint point,
    PointerKind kind = PointerKind.Click,
    Guid? gestureId = null,
    string? text = null) : EventArgs
{
    public NormalizedPoint Point { get; } = point;

    public PointerKind Kind { get; } = kind;

    public Guid? GestureId { get; } = gestureId;

    public string? Text { get; } = text;
}
