using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Contracts.Messages;

/// <summary>
/// A single pointer event on its way from an annotator to the host. <paramref name="AnnotatorId"/>
/// is stamped by the relay on the way out, so the host can tell which of its annotators drew
/// this; whatever an annotator puts there is replaced and never reaches the host.
/// </summary>
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
    string? Text = null,
    NormalizedPoint[]? PathPoints = null,
    string? AnnotatorId = null);
