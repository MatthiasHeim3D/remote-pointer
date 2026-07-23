using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

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

public sealed record ResumeSessionResult(
    SessionCredential Credential,
    SessionStateMessage State,
    string? ReplacedConnectionId);

public sealed record SessionTerminationResult(
    string SessionId,
    IReadOnlyList<string> ConnectionIds,
    long PointerCount);
