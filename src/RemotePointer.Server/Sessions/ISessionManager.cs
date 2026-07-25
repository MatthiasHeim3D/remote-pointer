using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

public interface ISessionManager
{
    int ActiveSessionCount { get; }

    bool ReceiverDiscoveryEnabled { get; }

    bool ServerPasswordRequired { get; }

    RelayGroupChange SetConnectionGroup(string connectionId, string? groupKey);

    string GetConnectionGroup(string connectionId);

    CreateSessionResponse CreateReceiverSession(
        DisplayDescriptor display,
        string connectionId,
        string clientInstanceId,
        string receiverDisplayName,
        string? applicationInstanceId = null,
        ClientProfile? profile = null,
        int? maximumPresenterConnections = null);

    IReadOnlyList<AvailableReceiverDescriptor> GetAvailableReceivers(
        string? excludedApplicationInstanceId = null,
        string? connectionId = null);

    bool SetReceiverDiscoverable(
        string sessionId,
        string receiverConnectionId,
        bool discoverable);

    JoinSessionResult RequestToJoinSession(
        JoinRequest request,
        string connectionId,
        string displayName,
        string? applicationInstanceId = null);

    JoinSessionResult RequestToJoinReceiver(
        DirectJoinRequest request,
        string connectionId,
        string displayName,
        string? applicationInstanceId = null);

    ReceiverDisplayUpdateResult UpdateReceiverDisplay(
        string sessionId,
        string receiverConnectionId,
        DisplayDescriptor display);

    ReceiverClientSettingsUpdateResult UpdateReceiverClientSettings(
        string sessionId,
        string receiverConnectionId,
        string receiverDisplayName,
        ClientProfile profile,
        int maximumPresenterConnections);

    ApprovePresenterResult ApprovePresenter(
        string sessionId,
        string presenterConnectionId,
        string receiverConnectionId);

    RejectPresenterResult RejectPresenter(
        string sessionId,
        string presenterConnectionId,
        string receiverConnectionId);

    PointerRelayResult AcceptPointer(string connectionId, PointerEventMessage pointerEvent);

    AcknowledgementRelayResult AcceptAcknowledgement(
        string connectionId,
        PointerAcknowledgement acknowledgement);

    ResumeSessionResult ResumeSession(
        string connectionId,
        SessionResumeRequest request,
        string? applicationInstanceId = null);

    SessionTerminationResult DisconnectPresenters(
        string sessionId,
        string receiverConnectionId);

    SessionTerminationResult EndSession(string sessionId, string connectionId);

    IReadOnlyList<SessionTerminationResult> CollectExpiredSessions();

    ConnectionDisconnectResult? Disconnect(string connectionId);
}
