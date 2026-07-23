using System.Reflection;
using System.Net.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemotePointer.Client.Configuration;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Serialization;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Client.Services;

public sealed class SignalRRelayClient : IRelayClient
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly HubConnection connection;
    private readonly string clientInstanceId;
    private readonly SynchronizationContext? synchronizationContext;
    private readonly object stateLock = new();
    private bool disposed;
    private SessionCredential? credential;
    private string? sessionId;
    private RelayConnectionStatus status = RelayConnectionStatus.Disconnected;

    public SignalRRelayClient(
        ClientSettings settings,
        IClientInstanceIdProvider clientInstanceIdProvider,
        Func<HttpMessageHandler>? messageHandlerFactory = null,
        HttpTransportType? transport = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clientInstanceIdProvider);

        ServerUrl = settings.Server.BaseUrl.TrimEnd('/');
        clientInstanceId = clientInstanceIdProvider.GetClientInstanceId();
        synchronizationContext = SynchronizationContext.Current;
        var hubUrl = $"{ServerUrl}/hubs/pointer"
            + $"?clientInstanceId={Uri.EscapeDataString(clientInstanceId)}"
            + $"&displayName={Uri.EscapeDataString(Environment.MachineName)}";
        var reconnectDelays = settings.Server.ReconnectDelaysSeconds
            .Select(delay => TimeSpan.FromSeconds(delay))
            .ToArray();

        var builder = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options =>
                {
                    if (messageHandlerFactory is not null)
                    {
                        options.HttpMessageHandlerFactory = _ => messageHandlerFactory();
                    }

                    if (transport is not null)
                    {
                        options.Transports = transport.Value;
                    }
                })
            .WithAutomaticReconnect(reconnectDelays)
            .AddJsonProtocol(
                options => RemotePointerJson.Configure(options.PayloadSerializerOptions));
        connection = builder.Build();
        RegisterCallbacks();
    }

    public event EventHandler<RelayConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

    public event EventHandler<PresenterJoinRequestedEventArgs>? PresenterJoinRequested;

    public event EventHandler<RelaySessionStateEventArgs>? SessionApproved;

    public event EventHandler<RelayPointerEventArgs>? PointerReceived;

    public event EventHandler<RelayAcknowledgementEventArgs>? PointerDisplayed;

    public event EventHandler<RelaySessionEndedEventArgs>? SessionEnded;

    public string ServerUrl { get; }

    public RelayConnectionStatus Status
    {
        get
        {
            lock (stateLock)
            {
                return status;
            }
        }
    }

    public string? SessionId
    {
        get
        {
            lock (stateLock)
            {
                return sessionId;
            }
        }
    }

    public SessionCredential? Credential
    {
        get
        {
            lock (stateLock)
            {
                return credential;
            }
        }
    }

    public async Task<CreateSessionResponse> CreateReceiverSessionAsync(
        DisplayDescriptor display,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(display));
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var response = await connection.InvokeAsync<CreateSessionResponse>(
                "CreateReceiverSession",
                display,
                cancellationToken)
            .ConfigureAwait(false);
        SetSession(response.SessionId, response.Credential);
        return response;
    }

    public async Task<JoinResponse> RequestToJoinSessionAsync(
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var request = new JoinRequest(
            pairingCode,
            ClientRole.Presenter,
            clientInstanceId,
            GetClientVersion());
        var response = await connection.InvokeAsync<JoinResponse>(
                "RequestToJoinSession",
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Accepted)
        {
            lock (stateLock)
            {
                sessionId = response.SessionId;
            }
        }

        return response;
    }

    public async Task ApprovePresenterAsync(
        string sessionId,
        string presenterConnectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await connection.InvokeAsync(
                "ApprovePresenter",
                sessionId,
                presenterConnectionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SendPointerAsync(
        PointerEventMessage pointerEvent,
        CancellationToken cancellationToken = default)
    {
        var currentCredential = Credential;
        if (!CanSend(ClientRole.Presenter, currentCredential)
            || !string.Equals(
                pointerEvent.SessionId,
                currentCredential!.SessionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await connection.InvokeAsync(
                    "SendPointer",
                    pointerEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (connection.State != HubConnectionState.Connected)
        {
            return false;
        }
    }

    public async Task<bool> AcknowledgePointerAsync(
        PointerAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        if (!CanSend(ClientRole.Receiver, Credential))
        {
            return false;
        }

        try
        {
            await connection.InvokeAsync(
                    "AcknowledgePointer",
                    acknowledgement,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (connection.State != HubConnectionState.Connected)
        {
            return false;
        }
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        var currentSessionId = SessionId;
        if (currentSessionId is null || connection.State != HubConnectionState.Connected)
        {
            ClearSession();
            return;
        }

        await connection.InvokeAsync("EndSession", currentSessionId, cancellationToken)
            .ConfigureAwait(false);
        ClearSession();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await connection.StopAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        connectionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RegisterCallbacks()
    {
        connection.On<PresenterDescriptor>(
            "PresenterJoinRequested",
            presenter => Publish(
                () => PresenterJoinRequested?.Invoke(
                    this,
                    new PresenterJoinRequestedEventArgs(presenter))));
        connection.On<SessionCredential>(
            "SessionCredentialIssued",
            issuedCredential => SetSession(issuedCredential.SessionId, issuedCredential));
        connection.On<SessionStateMessage>(
            "SessionApproved",
            state => Publish(
                () => SessionApproved?.Invoke(this, new RelaySessionStateEventArgs(state))));
        connection.On<PointerEventMessage>(
            "PointerReceived",
            pointerEvent => Publish(
                () => PointerReceived?.Invoke(this, new RelayPointerEventArgs(pointerEvent))));
        connection.On<PointerAcknowledgement>(
            "PointerDisplayed",
            acknowledgement => Publish(
                () => PointerDisplayed?.Invoke(
                    this,
                    new RelayAcknowledgementEventArgs(acknowledgement))));
        connection.On<string>(
            "SessionEnded",
            reason =>
            {
                var expired = reason.Contains("expired", StringComparison.OrdinalIgnoreCase);
                ClearSession();
                SetStatus(
                    expired ? RelayConnectionStatus.SessionExpired : RelayConnectionStatus.Connected,
                    reason);
                Publish(
                    () => SessionEnded?.Invoke(
                        this,
                        new RelaySessionEndedEventArgs(reason, expired)));
            });

        connection.Reconnecting += exception =>
        {
            SetStatus(
                RelayConnectionStatus.Reconnecting,
                exception is null ? "Reconnecting to relay." : "Relay connection interrupted; reconnecting.");
            return Task.CompletedTask;
        };
        connection.Reconnected += OnReconnectedAsync;
        connection.Closed += exception =>
        {
            if (!disposed)
            {
                SetStatus(
                    RelayConnectionStatus.Disconnected,
                    exception is null ? "Disconnected from relay." : "Relay connection failed.");
            }

            return Task.CompletedTask;
        };
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (connection.State == HubConnectionState.Connected)
        {
            return;
        }

        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.State == HubConnectionState.Disconnected)
            {
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(RelayConnectionStatus.Connected, "Connected to relay.");
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task OnReconnectedAsync(string? connectionId)
    {
        _ = connectionId;
        var currentCredential = Credential;
        if (currentCredential is null)
        {
            SetStatus(RelayConnectionStatus.Connected, "Reconnected to relay.");
            return;
        }

        try
        {
            var resumedCredential = await connection.InvokeAsync<SessionCredential>(
                    "ResumeSession",
                    new SessionResumeRequest(
                        currentCredential.SessionId,
                        currentCredential.Role,
                        currentCredential.ClientInstanceId,
                        currentCredential.SessionToken,
                        currentCredential.ReconnectToken))
                .ConfigureAwait(false);
            SetSession(resumedCredential.SessionId, resumedCredential);
            SetStatus(RelayConnectionStatus.Connected, "Reconnected and resumed session.");
        }
        catch (Exception)
        {
            ClearSession();
            SetStatus(
                RelayConnectionStatus.SessionExpired,
                "The previous session could not be resumed.");
            Publish(
                () => SessionEnded?.Invoke(
                    this,
                    new RelaySessionEndedEventArgs(
                        "The previous session could not be resumed.",
                        expired: true)));
        }
    }

    private bool CanSend(ClientRole role, SessionCredential? currentCredential) =>
        Status == RelayConnectionStatus.Connected
        && connection.State == HubConnectionState.Connected
        && currentCredential?.Role == role;

    private void SetSession(string newSessionId, SessionCredential newCredential)
    {
        lock (stateLock)
        {
            sessionId = newSessionId;
            credential = newCredential;
        }
    }

    private void ClearSession()
    {
        lock (stateLock)
        {
            sessionId = null;
            credential = null;
        }
    }

    private void SetStatus(RelayConnectionStatus newStatus, string message)
    {
        lock (stateLock)
        {
            status = newStatus;
        }

        Publish(
            () => ConnectionStatusChanged?.Invoke(
                this,
                new RelayConnectionStatusChangedEventArgs(newStatus, message)));
    }

    private void Publish(Action action)
    {
        if (synchronizationContext is null)
        {
            action();
            return;
        }

        synchronizationContext.Post(_ => action(), null);
    }

    private static string GetClientVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }

    private static void EnsureValid(ValidationResult result)
    {
        if (!result.IsValid)
        {
            throw new ArgumentException(result.Errors[0].Message);
        }
    }
}
