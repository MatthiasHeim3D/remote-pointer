using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

public interface ISessionManager
{
    int ActiveSessionCount { get; }

    CreateSessionResponse CreateReceiverSession(
        DisplayDescriptor display,
        string connectionId,
        string clientInstanceId);

    JoinSessionResult RequestToJoinSession(
        JoinRequest request,
        string connectionId,
        string displayName);

    ApprovePresenterResult ApprovePresenter(
        string sessionId,
        string presenterConnectionId,
        string receiverConnectionId);

    PointerRelayResult AcceptPointer(string connectionId, PointerEventMessage pointerEvent);

    AcknowledgementRelayResult AcceptAcknowledgement(
        string connectionId,
        PointerAcknowledgement acknowledgement);

    ResumeSessionResult ResumeSession(string connectionId, SessionResumeRequest request);

    SessionTerminationResult EndSession(string sessionId, string connectionId);

    IReadOnlyList<SessionTerminationResult> CollectExpiredSessions();

    void Disconnect(string connectionId);
}
