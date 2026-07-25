using Microsoft.AspNetCore.SignalR;
using RemotePointer.Contracts.Messages;
using RemotePointer.Server.Sessions;
using RemotePointer.Server.Security;

namespace RemotePointer.Server.Hubs;

public sealed class PointerHub(
    ISessionManager sessionManager,
    ILogger<PointerHub> logger) : Hub<IPointerClient>
{
    public RelayCapabilities GetRelayCapabilities() =>
        new(sessionManager.ServerPasswordRequired);

    /// <summary>
    /// Places this connection in the group its server password derives to. The argument is the
    /// derived key, never the password: the relay cannot recover the password from it and does
    /// not log it.
    /// </summary>
    public async Task EnterRelayGroup(string groupKey)
    {
        try
        {
            var change = sessionManager.SetConnectionGroup(Context.ConnectionId, groupKey);
            if (change.PreviousGroupKey is not null)
            {
                await Groups.RemoveFromGroupAsync(
                        Context.ConnectionId,
                        DirectoryGroupName(change.PreviousGroupKey))
                    .ConfigureAwait(false);
            }

            await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    DirectoryGroupName(change.GroupKey))
                .ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "EnterRelayGroup");
        }
    }

    public IReadOnlyList<AvailableReceiverDescriptor> GetAvailableReceivers() =>
        sessionManager.GetAvailableReceivers(GetApplicationInstanceId(), Context.ConnectionId);

    public override async Task OnConnectedAsync()
    {
        // Every connection starts in the open pool so that a client which never presents a
        // password still receives directory notifications on a relay that allows one. Entering
        // a group moves the connection out of it.
        await Groups.AddToGroupAsync(
                Context.ConnectionId,
                DirectoryGroupName(SessionManager.OpenGroupKey))
            .ConfigureAwait(false);
        logger.LogInformation(
            AuditEventIds.ClientConnected,
            "Relay client connected. ConnectionId={ConnectionId} ClientInstanceId={ClientInstanceId}",
            Context.ConnectionId,
            GetOptionalQueryValue("clientInstanceId"));
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Read before disconnecting: the connection's group is released with it, and the peers
        // that need to hear about this departure are the ones that shared it.
        var groupKey = sessionManager.GetConnectionGroup(Context.ConnectionId);
        var disconnect = sessionManager.Disconnect(Context.ConnectionId);
        if (disconnect is not null)
        {
            foreach (var presenterConnectionId in disconnect.PresenterConnectionIdsToEnd)
            {
                await Groups.RemoveFromGroupAsync(
                        presenterConnectionId,
                        GroupName(disconnect.SessionId))
                    .ConfigureAwait(false);
                await Clients.Client(presenterConnectionId)
                    .SessionEnded("The receiver connection ended. Request access again after it reconnects.")
                    .ConfigureAwait(false);
            }

            if (disconnect.ReceiverConnectionId is not null && disconnect.State is not null)
            {
                if (disconnect.CancelledPresenterRequestConnectionId is not null)
                {
                    await Clients.Client(disconnect.ReceiverConnectionId)
                        .PresenterJoinCancelled(
                            disconnect.CancelledPresenterRequestConnectionId)
                        .ConfigureAwait(false);
                }

                await Clients.Client(disconnect.ReceiverConnectionId)
                    .SessionApproved(disconnect.State)
                    .ConfigureAwait(false);
            }
        }

        await NotifyDirectoryChangedAsync(groupKey).ConfigureAwait(false);
        if (exception is null)
        {
            logger.LogInformation(
                AuditEventIds.ClientDisconnected,
                "Relay client disconnected. ConnectionId={ConnectionId}",
                Context.ConnectionId);
        }
        else
        {
            logger.LogWarning(
                AuditEventIds.ClientDisconnected,
                exception,
                "Relay client disconnected with an error. ConnectionId={ConnectionId}",
                Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public async Task<CreateSessionResponse> CreateReceiverSession(
        DisplayDescriptor display,
        ClientProfile profile,
        int maximumPresenterConnections,
        string displayName)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        try
        {
            var response = sessionManager.CreateReceiverSession(
                display,
                Context.ConnectionId,
                clientInstanceId,
                string.IsNullOrWhiteSpace(displayName)
                    ? GetDisplayName(clientInstanceId)
                    : displayName.Trim(),
                GetApplicationInstanceId(),
                profile,
                maximumPresenterConnections);
            await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    GroupName(response.SessionId))
                .ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionCreated,
                "Receiver session created. SessionId={SessionId} ClientInstanceId={ClientInstanceId} ExpiresAt={ExpiresAt}",
                response.SessionId,
                clientInstanceId,
                response.Credential.ExpiresAt);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            return response;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "CreateReceiverSession");
        }
    }

    public async Task<bool> SetReceiverDiscoverable(string sessionId, bool discoverable)
    {
        try
        {
            var result = sessionManager.SetReceiverDiscoverable(
                sessionId,
                Context.ConnectionId,
                discoverable);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            return result;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "SetReceiverDiscoverable");
        }
    }

    public async Task<JoinResponse> RequestToJoinReceiver(
        DirectJoinRequest request,
        string displayName)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        if (!string.Equals(request.ClientInstanceId, clientInstanceId, StringComparison.Ordinal))
        {
            LogValidationFailure("client_identity_mismatch", "RequestToJoinReceiver");
            return new JoinResponse(false, null, "The join request identity is invalid.");
        }

        try
        {
            var result = sessionManager.RequestToJoinReceiver(
                request,
                Context.ConnectionId,
                string.IsNullOrWhiteSpace(displayName)
                    ? GetDisplayName(clientInstanceId)
                    : displayName.Trim(),
                GetApplicationInstanceId());
            if (result.Response.Accepted
                && result.ReceiverConnectionId is not null
                && result.Presenter is not null)
            {
                await Clients.Client(result.ReceiverConnectionId)
                    .PresenterJoinRequested(result.Presenter)
                    .ConfigureAwait(false);
                await NotifyDirectoryChangedAsync().ConfigureAwait(false);
                logger.LogInformation(
                    AuditEventIds.PresenterJoinRequested,
                    "Direct presenter join requested. SessionId={SessionId} PresenterClientInstanceId={ClientInstanceId}",
                    result.Response.SessionId,
                    request.ClientInstanceId);
            }
            else
            {
                logger.LogWarning(
                    AuditEventIds.PresenterJoinRejected,
                    "Direct presenter join rejected. ClientInstanceId={ClientInstanceId} Reason={Reason}",
                    request.ClientInstanceId,
                    result.Response.Reason);
            }

            return result.Response;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "RequestToJoinReceiver");
        }
    }

    public async Task UpdateReceiverDisplay(string sessionId, DisplayDescriptor display)
    {
        try
        {
            var result = sessionManager.UpdateReceiverDisplay(
                sessionId,
                Context.ConnectionId,
                display);
            foreach (var presenterConnectionId in result.PresenterConnectionIds)
            {
                await Clients.Client(presenterConnectionId)
                    .ReceiverDisplayChanged(result.Display)
                    .ConfigureAwait(false);
            }
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "UpdateReceiverDisplay");
        }
    }

    public async Task UpdateReceiverClientSettings(
        string sessionId,
        string displayName,
        ClientProfile profile,
        int maximumPresenterConnections)
    {
        try
        {
            var result = sessionManager.UpdateReceiverClientSettings(
                sessionId,
                Context.ConnectionId,
                displayName,
                profile,
                maximumPresenterConnections);
            await Clients.Client(result.ReceiverConnectionId)
                .SessionApproved(result.State)
                .ConfigureAwait(false);
            foreach (var presenterConnectionId in result.PresenterConnectionIds)
            {
                await Clients.Client(presenterConnectionId)
                    .SessionApproved(result.State)
                    .ConfigureAwait(false);
            }

            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "UpdateReceiverClientSettings");
        }
    }

    public async Task ApprovePresenter(string sessionId, string presenterConnectionId)
    {
        try
        {
            var result = sessionManager.ApprovePresenter(
                sessionId,
                presenterConnectionId,
                Context.ConnectionId);
            await Groups.AddToGroupAsync(
                    result.PresenterConnectionId,
                    GroupName(result.SessionId))
                .ConfigureAwait(false);
            await Clients.Client(result.PresenterConnectionId)
                .SessionCredentialIssued(result.PresenterCredential)
                .ConfigureAwait(false);
            await Clients.Client(result.PresenterConnectionId)
                .SessionApproved(result.State)
                .ConfigureAwait(false);
            await Clients.Client(result.ReceiverConnectionId)
                .SessionApproved(result.State)
                .ConfigureAwait(false);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.PresenterApproved,
                "Presenter approved. SessionId={SessionId} PresenterClientInstanceId={ClientInstanceId}",
                result.SessionId,
                result.PresenterCredential.ClientInstanceId);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "ApprovePresenter");
        }
    }

    public async Task RejectPresenter(string sessionId, string presenterConnectionId)
    {
        try
        {
            var result = sessionManager.RejectPresenter(
                sessionId,
                presenterConnectionId,
                Context.ConnectionId);
            await Clients.Client(result.PresenterConnectionId)
                .SessionEnded("Connection request declined by receiver.")
                .ConfigureAwait(false);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.PresenterJoinRejected,
                "Presenter join declined. SessionId={SessionId} PresenterConnectionId={PresenterConnectionId}",
                result.SessionId,
                result.PresenterConnectionId);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "RejectPresenter");
        }
    }

    public async Task SendPointer(PointerEventMessage pointerEvent)
    {
        try
        {
            var result = sessionManager.AcceptPointer(Context.ConnectionId, pointerEvent);
            if (result.Disposition == PointerRelayDisposition.Accepted
                && result.ReceiverConnectionId is not null)
            {
                await Clients.Client(result.ReceiverConnectionId)
                    .PointerReceived(pointerEvent)
                    .ConfigureAwait(false);
            }
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "SendPointer");
        }
    }

    public async Task AcknowledgePointer(PointerAcknowledgement acknowledgement)
    {
        try
        {
            var result = sessionManager.AcceptAcknowledgement(
                Context.ConnectionId,
                acknowledgement);
            if (result.PresenterConnectionId is not null)
            {
                await Clients.Client(result.PresenterConnectionId)
                    .PointerDisplayed(acknowledgement)
                    .ConfigureAwait(false);
            }
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "AcknowledgePointer");
        }
    }

    public async Task<SessionCredential> ResumeSession(SessionResumeRequest request)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        if (!string.Equals(
                request.ClientInstanceId,
                clientInstanceId,
                StringComparison.Ordinal))
        {
            throw new HubException("The resume request identity is invalid.");
        }

        try
        {
            var result = sessionManager.ResumeSession(
                Context.ConnectionId,
                request,
                GetApplicationInstanceId());
            if (result.ReplacedConnectionId is not null)
            {
                await Groups.RemoveFromGroupAsync(
                        result.ReplacedConnectionId,
                        GroupName(request.SessionId))
                    .ConfigureAwait(false);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(request.SessionId))
                .ConfigureAwait(false);
            await Clients.Caller.SessionApproved(result.State).ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionResumed,
                "Session participant resumed. SessionId={SessionId} Role={Role} ClientInstanceId={ClientInstanceId}",
                request.SessionId,
                request.Role,
                request.ClientInstanceId);
            return result.Credential;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "ResumeSession");
        }
    }

    public async Task EndSession(string sessionId)
    {
        try
        {
            var result = sessionManager.EndSession(sessionId, Context.ConnectionId);
            if (result.ReceiverPreserved && result.State is not null)
            {
                var cancelledRequestConnectionId =
                    result.CancelledPresenterRequestConnectionId;
                if (cancelledRequestConnectionId is not null
                    && result.ReceiverConnectionId is not null)
                {
                    await Clients.Client(result.ReceiverConnectionId)
                        .PresenterJoinCancelled(cancelledRequestConnectionId)
                        .ConfigureAwait(false);
                }

                foreach (var presenterConnectionId in GetPresenterConnectionIds(result))
                {
                    await Groups.RemoveFromGroupAsync(
                            presenterConnectionId,
                            GroupName(sessionId))
                        .ConfigureAwait(false);
                    await Clients.Client(presenterConnectionId)
                        .SessionEnded(
                            string.Equals(
                                presenterConnectionId,
                                cancelledRequestConnectionId,
                                StringComparison.Ordinal)
                                ? "Connection request cancelled."
                                : "Disconnected from the receiver.")
                        .ConfigureAwait(false);
                }

                if (result.ReceiverConnectionId is not null)
                {
                    await Clients.Client(result.ReceiverConnectionId)
                        .SessionApproved(result.State)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await Clients.Group(GroupName(sessionId))
                    .SessionEnded("The receiver connection ended and is no longer available.")
                    .ConfigureAwait(false);
            }
            logger.LogInformation(
                AuditEventIds.SessionEnded,
                "Connection ended. SessionId={SessionId} ReceiverPreserved={ReceiverPreserved} PointerCount={PointerCount}",
                result.SessionId,
                result.ReceiverPreserved,
                result.PointerCount);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "EndSession");
        }
    }

    public async Task DisconnectAllConnections(string sessionId)
    {
        try
        {
            var result = sessionManager.DisconnectPresenters(
                sessionId,
                Context.ConnectionId);
            foreach (var presenterConnectionId in GetPresenterConnectionIds(result))
            {
                await Groups.RemoveFromGroupAsync(
                        presenterConnectionId,
                        GroupName(sessionId))
                    .ConfigureAwait(false);
                await Clients.Client(presenterConnectionId)
                    .SessionEnded("Disconnected by the receiver.")
                    .ConfigureAwait(false);
            }

            if (result.ReceiverConnectionId is not null && result.State is not null)
            {
                await Clients.Client(result.ReceiverConnectionId)
                    .SessionApproved(result.State)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                AuditEventIds.SessionEnded,
                "Receiver disconnected all presenters. SessionId={SessionId} PointerCount={PointerCount}",
                result.SessionId,
                result.PointerCount);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "DisconnectAllConnections");
        }
    }

    private HubException ToHubException(SessionOperationException exception, string operation)
    {
        LogValidationFailure(exception.Code, operation);
        return new HubException(exception.Message);
    }

    private static IReadOnlyList<string> GetPresenterConnectionIds(
        SessionTerminationResult result) =>
        result.PresenterConnectionIds
        ?? (result.PresenterConnectionId is null ? [] : [result.PresenterConnectionId]);

    private void LogValidationFailure(string code, string operation) =>
        logger.LogWarning(
            AuditEventIds.OperationRejected,
            "Relay operation rejected. Operation={Operation} Code={Code} ConnectionId={ConnectionId}",
            operation,
            code,
            Context.ConnectionId);

    private string GetRequiredClientInstanceId()
    {
        var clientInstanceId = GetOptionalQueryValue("clientInstanceId");
        if (string.IsNullOrWhiteSpace(clientInstanceId) || clientInstanceId.Length > 128)
        {
            throw new HubException("A valid clientInstanceId connection parameter is required.");
        }

        return clientInstanceId;
    }

    private string GetApplicationInstanceId()
    {
        var applicationInstanceId = GetOptionalQueryValue("applicationInstanceId");
        return string.IsNullOrWhiteSpace(applicationInstanceId) || applicationInstanceId.Length > 128
            ? GetRequiredClientInstanceId()
            : applicationInstanceId;
    }

    private string GetDisplayName(string fallback)
    {
        var displayName = GetOptionalQueryValue("displayName");
        return string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128
            ? fallback
            : displayName;
    }

    private string? GetOptionalQueryValue(string key)
    {
        var value = Context.GetHttpContext()?.Request.Query[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GroupName(string sessionId) => $"session:{sessionId}";

    /// <summary>
    /// Directory changes reach only the clients that share the same server password. Nobody
    /// else can see the affected receivers, and one client's connection churn no longer costs
    /// a directory read on every other connection.
    /// </summary>
    private static string DirectoryGroupName(string groupKey) => $"directory:{groupKey}";

    private Task NotifyDirectoryChangedAsync() =>
        NotifyDirectoryChangedAsync(sessionManager.GetConnectionGroup(Context.ConnectionId));

    private Task NotifyDirectoryChangedAsync(string groupKey) =>
        Clients.Group(DirectoryGroupName(groupKey)).ReceiverDirectoryChanged();
}
