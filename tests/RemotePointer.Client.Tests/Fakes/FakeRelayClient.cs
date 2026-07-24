using RemotePointer.Client.Services;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Fakes;

internal sealed class FakeRelayClient : IRelayClient
{
    public event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    public event EventHandler<PresenterJoinRequestedEventArgs>? PresenterJoinRequested;

    public event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    public event EventHandler<RelayReceiverDisplayChangedEventArgs>? ReceiverDisplayChanged;

    public event EventHandler<RelayPointerEventArgs>? PointerReceived;

    public event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    public event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    public string ServerUrl { get; init; } = "https://relay.example";

    public RelayConnectionStatus Status { get; private set; } = RelayConnectionStatus.Connected;

    public string? SessionId { get; set; }

    public SessionCredential? Credential { get; set; }

    public CreateSessionResponse? CreateResponse { get; set; }

    public JoinResponse JoinResponse { get; set; } = new(true, "session-1", null);

    public RelayCapabilities Capabilities { get; set; } = new(false);

    public IReadOnlyList<AvailableReceiverDescriptor> AvailableReceivers { get; set; } = [];

    public bool IsDiscoverable { get; private set; }

    public DisplayDescriptor? UpdatedReceiverDisplay { get; private set; }

    public string? RequestedReceiverSessionId { get; private set; }

    public int CreateCount { get; private set; }

    public PresenterDescriptor? ApprovedPresenter { get; private set; }

    public PointerEventMessage? SentPointer { get; private set; }

    public PointerAcknowledgement? SentAcknowledgement { get; private set; }

    public int EndCount { get; private set; }

    public Exception? EndException { get; set; }

    public bool Disposed { get; private set; }

    public bool ResumeResult { get; set; }

    public int ResumeCount { get; private set; }

    public Task<RelayCapabilities> GetRelayCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Capabilities);
    }

    public Task<IReadOnlyList<AvailableReceiverDescriptor>> GetAvailableReceiversAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(AvailableReceivers);
    }

    public Task<bool> TryResumeSessionAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ResumeCount++;
        return Task.FromResult(ResumeResult);
    }

    public Task<CreateSessionResponse> CreateReceiverSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        _ = display;
        _ = cancellationToken;
        CreateCount++;
        var response = CreateResponse ?? throw new InvalidOperationException("No create response configured.");
        SessionId = response.SessionId;
        Credential = response.Credential;
        return Task.FromResult(response);
    }

    public Task<bool> SetReceiverDiscoverableAsync(
        bool discoverable,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        IsDiscoverable = discoverable;
        return Task.FromResult(discoverable);
    }

    public Task<JoinResponse> RequestToJoinReceiverAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        RequestedReceiverSessionId = sessionId;
        _ = cancellationToken;
        SessionId = JoinResponse.SessionId;
        return Task.FromResult(JoinResponse);
    }

    public Task UpdateReceiverDisplayAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        UpdatedReceiverDisplay = display;
        return Task.CompletedTask;
    }

    public Task ApprovePresenterAsync(
        string sessionId,
        string presenterConnectionId,
        CancellationToken cancellationToken = default)
    {
        _ = sessionId;
        _ = cancellationToken;
        ApprovedPresenter = new PresenterDescriptor(
            presenterConnectionId,
            "presenter-id",
            "Presenter",
            "1.0.0");
        return Task.CompletedTask;
    }

    public Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        SentPointer = pointerEvent;
        return Task.FromResult(Status == RelayConnectionStatus.Connected);
    }

    public Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        SentAcknowledgement = acknowledgement;
        return Task.FromResult(Status == RelayConnectionStatus.Connected);
    }

    public Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        EndCount++;
        if (EndException is not null)
        {
            return Task.FromException(EndException);
        }

        SessionId = null;
        Credential = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void RaiseConnectionStatus(RelayConnectionStatus status, string message)
    {
        Status = status;
        ConnectionStatusChanged?.Invoke(
            this,
            new RelayConnectionStatusChangedEventArgs(status, message));
    }

    public void RaiseJoinRequest(PresenterDescriptor presenter) =>
        PresenterJoinRequested?.Invoke(this, new PresenterJoinRequestedEventArgs(presenter));

    public void RaiseApproved(SessionStateMessage state)
    {
        SessionId = state.SessionId;
        Credential ??= new SessionCredential(
            state.SessionId,
            ClientRole.Presenter,
            "presenter-id",
            new string('s', 32),
            new string('r', 32),
            state.ExpiresAt);
        SessionApproved?.Invoke(this, new RelaySessionStateEventArgs(state));
    }

    public void RaiseReceiverDisplayChanged(DisplayDescriptor display) =>
        ReceiverDisplayChanged?.Invoke(
            this,
            new RelayReceiverDisplayChangedEventArgs(display));

    public void RaisePointer(PointerEventMessage pointerEvent) =>
        PointerReceived?.Invoke(this, new RelayPointerEventArgs(pointerEvent));

    public void RaiseAcknowledgement(PointerAcknowledgement acknowledgement) =>
        PointerDisplayed?.Invoke(this, new RelayAcknowledgementEventArgs(acknowledgement));

    public void RaiseSessionEnded(string reason, bool expired = false)
    {
        SessionId = null;
        Credential = null;
        SessionEnded?.Invoke(this, new RelaySessionEndedEventArgs(reason, expired));
    }
}
