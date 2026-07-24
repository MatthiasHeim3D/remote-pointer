using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

public interface ISessionManager
{
    int ActiveSessionCount { get; }

    bool ReceiverDiscoveryEnabled { get; }

    CreateSessionResponse CreateReceiverSession(
        DisplayDescriptor display,
        string connectionId,
        string clientInstanceId,
        string receiverDisplayName);

    IReadOnlyList<AvailableReceiverDescriptor> GetAvailableReceivers();

    bool SetReceiverDiscoverable(
        string sessionId,
        string receiverConnectionId,
        bool discoverable);

    JoinSessionResult RequestToJoinSession(
        JoinRequest request,
        string connectionId,
        string displayName);

    JoinSessionResult RequestToJoinReceiver(
        DirectJoinRequest request,
        string connectionId,
        string displayName);

    ReceiverDisplayUpdateResult UpdateReceiverDisplay(
        string sessionId,
        string receiverConnectionId,
        DisplayDescriptor display);

    ApprovePresenterResult ApprovePresenter(
        string sessionId,
        string presenterConnectionId,
        string receiverConnectionId);

    PointerRelayResult AcceptPointer(string connectionId, PointerEventMessage pointerEvent);

    AcknowledgementRelayResult AcceptAcknowledgement(
        string connectionId,
        PointerAcknowledgement acknowledgement);

    ResumeSessionResult ResumeSession(string connectionId, SessionResumeRequest request);

    SessionTerminationResult DisconnectPresenters(
        string sessionId,
        string receiverConnectionId);

    SessionTerminationResult EndSession(string sessionId, string connectionId);

    IReadOnlyList<SessionTerminationResult> CollectExpiredSessions();

    void Disconnect(string connectionId);
}
