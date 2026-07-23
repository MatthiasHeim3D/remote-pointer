namespace RemotePointer.Contracts.Messages;

public sealed record PointerEventMessage(
    Guid EventId,
    string SessionId,
    long SequenceNumber,
    double NormalizedX,
    double NormalizedY,
    PointerKind Kind,
    long SentAtUnixMilliseconds,
    int TimeToLiveMilliseconds,
    Guid? GestureId = null,
    string? Text = null);
