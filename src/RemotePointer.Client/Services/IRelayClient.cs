using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public interface IRelayClient : IAsyncDisposable
{
    event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    event EventHandler<PresenterJoinRequestedEventArgs>? PresenterJoinRequested;

    event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    event EventHandler<RelayPointerEventArgs>? PointerReceived;

    event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    string ServerUrl { get; }

    RelayConnectionStatus Status { get; }

    string? SessionId { get; }

    SessionCredential? Credential { get; }

    Task<CreateSessionResponse> CreateReceiverSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default);

    Task<JoinResponse> RequestToJoinSessionAsync(
        string pairingCode,
        CancellationToken cancellationToken = default);

    Task ApprovePresenterAsync(
        string sessionId,
        string presenterConnectionId,
        CancellationToken cancellationToken = default);

    Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default);

    Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(CancellationToken cancellationToken = default);
}
