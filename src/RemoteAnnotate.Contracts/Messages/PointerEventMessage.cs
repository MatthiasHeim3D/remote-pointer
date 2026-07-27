using RemoteAnnotate.Contracts.Coordinates;

namespace RemoteAnnotate.Contracts.Messages;

/// <summary>
/// A single pointer event on its way from an annotator to the host. <paramref name="AnnotatorId"/>
/// is stamped by the relay on the way out, so the host can tell which of its annotators drew
/// this; whatever an annotator puts there is replaced and never reaches the host.
/// <paramref name="Color"/> is the opposite: the annotator chooses it and the relay passes it
/// through untouched, so the drawing looks the same on the host as it does under the hand that
/// made it. An event without one is drawn in <see cref="AnnotationColors.Default"/>.
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
    string? AnnotatorId = null,
    string? Color = null);
