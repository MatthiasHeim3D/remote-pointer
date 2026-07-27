using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Server.Sessions;

/// <summary>
/// The room a connection ended up in. <paramref name="PreviousRoom"/> is null when the
/// connection was already there, which is how a caller tells a move from a repeat.
/// </summary>
public sealed record RelayRoomChange(
    string Room,
    string? PreviousRoom,
    SessionTerminationResult? CancelledJoinRequest = null);

public sealed record JoinSessionResult(
    JoinResponse Response,
    string? HostConnectionId,
    AnnotatorDescriptor? Annotator);

public sealed record ApproveAnnotatorResult(
    string SessionId,
    string AnnotatorConnectionId,
    string HostConnectionId,
    SessionCredential AnnotatorCredential,
    SessionStateMessage State);

public sealed record RejectAnnotatorResult(
    string SessionId,
    string AnnotatorConnectionId,
    string HostConnectionId);

public enum PointerRelayDisposition
{
    Accepted,
    IgnoredSequence,

    /// <summary>
    /// The host paused this annotator. Its events are dropped rather than rejected: pausing is a
    /// host courtesy, not a protocol violation, and an event already in flight when the pause
    /// arrived should not surface as an error on the annotator.
    /// </summary>
    Paused,
}

/// <summary>
/// <paramref name="AnnotatorId"/> is the client instance id of the annotator the event came from;
/// the hub stamps it onto the message it forwards so the host can attribute the drawing.
/// </summary>
public sealed record PointerRelayResult(
    PointerRelayDisposition Disposition,
    string SessionId,
    string? HostConnectionId,
    string? AnnotatorId = null);

public sealed record AnnotatorPauseResult(
    string SessionId,
    IReadOnlyList<string> AnnotatorConnectionIds,
    string? HostConnectionId,
    SessionStateMessage State,
    bool Paused);

public sealed record AcknowledgementRelayResult(
    string SessionId,
    string? AnnotatorConnectionId);

/// <summary>
/// One annotator's newly effective drawing colour, to be delivered to that connection. Only
/// annotators whose colour actually moved are reported, so a busy session does not tell everybody
/// something they already know each time somebody joins or leaves.
/// </summary>
public sealed record AnnotationColorAssignment(string ConnectionId, string Color);

public sealed record HostDisplayUpdateResult(
    string SessionId,
    IReadOnlyList<string> AnnotatorConnectionIds,
    DisplayDescriptor Display)
{
    public string? AnnotatorConnectionId => AnnotatorConnectionIds.FirstOrDefault();
}

public sealed record HostClientSettingsUpdateResult(
    string HostConnectionId,
    IReadOnlyList<string> AnnotatorConnectionIds,
    SessionStateMessage State);

public sealed record ResumeSessionResult(
    SessionCredential Credential,
    SessionStateMessage State,
    string? ReplacedConnectionId);

/// <summary>
/// <paramref name="Room"/> is the room the affected session was published in, which is not
/// always the room of the connection that caused the change: an approved annotator that changes
/// rooms keeps its place in the session it was admitted to. It is the directory that gained or
/// lost an entry, so it is the one to notify.
/// </summary>
public sealed record SessionTerminationResult(
    string SessionId,
    IReadOnlyList<string> ConnectionIds,
    long PointerCount,
    string Room,
    bool HostPreserved = false,
    string? AnnotatorConnectionId = null,
    string? HostConnectionId = null,
    SessionStateMessage? State = null,
    IReadOnlyList<string>? AnnotatorConnectionIds = null,
    string? CancelledAnnotatorRequestConnectionId = null);

public sealed record ConnectionDisconnectResult(
    string SessionId,
    ClientRole DisconnectedRole,
    IReadOnlyList<string> AnnotatorConnectionIdsToEnd,
    string? HostConnectionId,
    SessionStateMessage? State,
    string Room,
    string? CancelledAnnotatorRequestConnectionId = null);
