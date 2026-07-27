using RemoteAnnotate.Contracts.Coordinates;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Services;

public sealed class PointerCapturedEventArgs(
    NormalizedPoint point,
    PointerKind kind = PointerKind.Click,
    Guid? gestureId = null,
    string? text = null,
    NormalizedPoint[]? pathPoints = null) : EventArgs
{
    public NormalizedPoint Point { get; } = point;

    public PointerKind Kind { get; } = kind;

    public Guid? GestureId { get; } = gestureId;

    public string? Text { get; } = text;

    public NormalizedPoint[]? PathPoints { get; } = pathPoints;
}
