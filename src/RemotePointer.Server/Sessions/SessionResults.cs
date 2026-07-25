using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

public sealed record RelayGroupChange(
    string GroupKey,
    string? PreviousGroupKey,
    SessionTerminationResult? CancelledJoinRequest = null);

public sealed record JoinSessionResult(
    JoinResponse Response,
    string? ReceiverConnectionId,
    PresenterDescriptor? Presenter);

public sealed record ApprovePresenterResult(
    string SessionId,
    string PresenterConnectionId,
    string ReceiverConnectionId,
    SessionCredential PresenterCredential,
    SessionStateMessage State);

public sealed record RejectPresenterResult(
    string SessionId,
    string PresenterConnectionId,
    string ReceiverConnectionId);

public enum PointerRelayDisposition
{
    Accepted,
    IgnoredSequence,
}

public sealed record PointerRelayResult(
    PointerRelayDisposition Disposition,
    string SessionId,
    string? ReceiverConnectionId);

public sealed record AcknowledgementRelayResult(
    string SessionId,
    string? PresenterConnectionId);

public sealed record ReceiverDisplayUpdateResult(
    string SessionId,
    IReadOnlyList<string> PresenterConnectionIds,
    DisplayDescriptor Display)
{
    public string? PresenterConnectionId => PresenterConnectionIds.FirstOrDefault();
}

public sealed record ReceiverClientSettingsUpdateResult(
    string ReceiverConnectionId,
    IReadOnlyList<string> PresenterConnectionIds,
    SessionStateMessage State);

public sealed record ResumeSessionResult(
    SessionCredential Credential,
    SessionStateMessage State,
    string? ReplacedConnectionId);

/// <summary>
/// <paramref name="GroupKey"/> is the group the affected session was published under, which is
/// not always the group of the connection that caused the change: an approved presenter that
/// changes its server password keeps its place in the session it was admitted to. It is the
/// directory that gained or lost an entry, so it is the one to notify.
/// </summary>
public sealed record SessionTerminationResult(
    string SessionId,
    IReadOnlyList<string> ConnectionIds,
    long PointerCount,
    string GroupKey,
    bool ReceiverPreserved = false,
    string? PresenterConnectionId = null,
    string? ReceiverConnectionId = null,
    SessionStateMessage? State = null,
    IReadOnlyList<string>? PresenterConnectionIds = null,
    string? CancelledPresenterRequestConnectionId = null);

public sealed record ConnectionDisconnectResult(
    string SessionId,
    ClientRole DisconnectedRole,
    IReadOnlyList<string> PresenterConnectionIdsToEnd,
    string? ReceiverConnectionId,
    SessionStateMessage? State,
    string GroupKey,
    string? CancelledPresenterRequestConnectionId = null);
