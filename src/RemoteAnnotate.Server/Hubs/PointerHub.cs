using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RemoteAnnotate.Contracts.Messages;
using RemoteAnnotate.Server.Sessions;
using RemoteAnnotate.Server.Security;

namespace RemoteAnnotate.Server.Hubs;

/// <summary>
/// Every method here is behind the relay's server password: a client that does not hold it is
/// turned away at negotiate and never reaches this hub.
/// </summary>
[Authorize]
public sealed class PointerHub(
    ISessionManager sessionManager,
    ServerPasswordVerifier passwordVerifier,
    ILogger<PointerHub> logger) : Hub<IPointerClient>
{
    public RelayCapabilities GetRelayCapabilities() =>
        new(passwordVerifier.IsRequired);

    /// <summary>
    /// Puts this connection in the room it names. The name is not a secret — the password at the
    /// front door is what decides who reaches the relay — so it is sent and held as typed.
    /// </summary>
    public async Task EnterRoom(string room)
    {
        try
        {
            var change = sessionManager.SetConnectionRoom(Context.ConnectionId, room);
            if (change.PreviousRoom is not null)
            {
                await Groups.RemoveFromGroupAsync(
                        Context.ConnectionId,
                        DirectoryGroupName(change.PreviousRoom))
                    .ConfigureAwait(false);
            }

            await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    DirectoryGroupName(change.Room))
                .ConfigureAwait(false);
            if (change.CancelledJoinRequest is not null)
            {
                await CancelJoinRequestAsync(change.CancelledJoinRequest).ConfigureAwait(false);
            }

            if (change.PreviousRoom is not null)
            {
                // Both directories change when a client moves between them: the one it left
                // can no longer see the host it published, and the one it joined can. The
                // caller is already in the new group, so this is also what refreshes its own
                // listing after a room change.
                await NotifyDirectoryChangedAsync(change.PreviousRoom).ConfigureAwait(false);
                await NotifyDirectoryChangedAsync(change.Room).ConfigureAwait(false);
            }
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "EnterRoom");
        }
    }

    public IReadOnlyList<AvailableHostDescriptor> GetAvailableHosts() =>
        sessionManager.GetAvailableHosts(GetApplicationInstanceId(), Context.ConnectionId);

    public override async Task OnConnectedAsync()
    {
        // Every connection starts in the default room, so a client that never names one still
        // receives directory notifications. Entering a room moves the connection out of it.
        await Groups.AddToGroupAsync(
                Context.ConnectionId,
                DirectoryGroupName(RoomName.Default))
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
        var room = sessionManager.GetConnectionRoom(Context.ConnectionId);
        var disconnect = sessionManager.Disconnect(Context.ConnectionId);
        if (disconnect is not null)
        {
            foreach (var annotatorConnectionId in disconnect.AnnotatorConnectionIdsToEnd)
            {
                await Groups.RemoveFromGroupAsync(
                        annotatorConnectionId,
                        GroupName(disconnect.SessionId))
                    .ConfigureAwait(false);
                await Clients.Client(annotatorConnectionId)
                    .SessionEnded("The host connection ended. Request access again after it reconnects.")
                    .ConfigureAwait(false);
            }

            if (disconnect.HostConnectionId is not null && disconnect.State is not null)
            {
                if (disconnect.CancelledAnnotatorRequestConnectionId is not null)
                {
                    await Clients.Client(disconnect.HostConnectionId)
                        .AnnotatorJoinCancelled(
                            disconnect.CancelledAnnotatorRequestConnectionId)
                        .ConfigureAwait(false);
                }

                await Clients.Client(disconnect.HostConnectionId)
                    .SessionApproved(disconnect.State)
                    .ConfigureAwait(false);
            }
        }

        await NotifyAnnotationColorsAsync(disconnect?.SessionId).ConfigureAwait(false);
        await NotifyDirectoryChangedAsync(room, disconnect?.Room).ConfigureAwait(false);
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

    public async Task<CreateSessionResponse> CreateHostSession(
        DisplayDescriptor display,
        ClientProfile profile,
        int maximumAnnotatorConnections,
        string displayName)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        try
        {
            var response = sessionManager.CreateHostSession(
                display,
                Context.ConnectionId,
                clientInstanceId,
                string.IsNullOrWhiteSpace(displayName)
                    ? GetDisplayName(clientInstanceId)
                    : displayName.Trim(),
                GetApplicationInstanceId(),
                profile,
                maximumAnnotatorConnections);
            await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    GroupName(response.SessionId))
                .ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionCreated,
                "Host session created. SessionId={SessionId} ClientInstanceId={ClientInstanceId} ExpiresAt={ExpiresAt}",
                response.SessionId,
                clientInstanceId,
                response.Credential.ExpiresAt);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            return response;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "CreateHostSession");
        }
    }

    public async Task<bool> SetHostDiscoverable(string sessionId, bool discoverable)
    {
        try
        {
            var result = sessionManager.SetHostDiscoverable(
                sessionId,
                Context.ConnectionId,
                discoverable);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            return result;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "SetHostDiscoverable");
        }
    }

    public async Task<JoinResponse> RequestToJoinHost(
        DirectJoinRequest request,
        string displayName)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        if (!string.Equals(request.ClientInstanceId, clientInstanceId, StringComparison.Ordinal))
        {
            LogValidationFailure("client_identity_mismatch", "RequestToJoinHost");
            return new JoinResponse(false, null, "The join request identity is invalid.");
        }

        try
        {
            var result = sessionManager.RequestToJoinHost(
                request,
                Context.ConnectionId,
                string.IsNullOrWhiteSpace(displayName)
                    ? GetDisplayName(clientInstanceId)
                    : displayName.Trim(),
                GetApplicationInstanceId());
            if (result.Response.Accepted
                && result.HostConnectionId is not null
                && result.Annotator is not null)
            {
                await Clients.Client(result.HostConnectionId)
                    .AnnotatorJoinRequested(result.Annotator)
                    .ConfigureAwait(false);
                await NotifyDirectoryChangedAsync().ConfigureAwait(false);
                logger.LogInformation(
                    AuditEventIds.AnnotatorJoinRequested,
                    "Direct annotator join requested. SessionId={SessionId} AnnotatorClientInstanceId={ClientInstanceId}",
                    result.Response.SessionId,
                    request.ClientInstanceId);
            }
            else
            {
                logger.LogWarning(
                    AuditEventIds.AnnotatorJoinRejected,
                    "Direct annotator join rejected. ClientInstanceId={ClientInstanceId} Reason={Reason}",
                    request.ClientInstanceId,
                    result.Response.Reason);
            }

            return result.Response;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "RequestToJoinHost");
        }
    }

    public async Task UpdateHostDisplay(string sessionId, DisplayDescriptor display)
    {
        try
        {
            var result = sessionManager.UpdateHostDisplay(
                sessionId,
                Context.ConnectionId,
                display);
            foreach (var annotatorConnectionId in result.AnnotatorConnectionIds)
            {
                await Clients.Client(annotatorConnectionId)
                    .HostDisplayChanged(result.Display)
                    .ConfigureAwait(false);
            }
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "UpdateHostDisplay");
        }
    }

    public async Task UpdateHostClientSettings(
        string sessionId,
        string displayName,
        ClientProfile profile,
        int maximumAnnotatorConnections)
    {
        try
        {
            var result = sessionManager.UpdateHostClientSettings(
                sessionId,
                Context.ConnectionId,
                displayName,
                profile,
                maximumAnnotatorConnections);
            await Clients.Client(result.HostConnectionId)
                .SessionApproved(result.State)
                .ConfigureAwait(false);
            foreach (var annotatorConnectionId in result.AnnotatorConnectionIds)
            {
                await Clients.Client(annotatorConnectionId)
                    .SessionApproved(result.State)
                    .ConfigureAwait(false);
            }

            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "UpdateHostClientSettings");
        }
    }

    public async Task ApproveAnnotator(string sessionId, string annotatorConnectionId)
    {
        try
        {
            var result = sessionManager.ApproveAnnotator(
                sessionId,
                annotatorConnectionId,
                Context.ConnectionId);
            await Groups.AddToGroupAsync(
                    result.AnnotatorConnectionId,
                    GroupName(result.SessionId))
                .ConfigureAwait(false);
            await Clients.Client(result.AnnotatorConnectionId)
                .SessionCredentialIssued(result.AnnotatorCredential)
                .ConfigureAwait(false);
            await Clients.Client(result.AnnotatorConnectionId)
                .SessionApproved(result.State)
                .ConfigureAwait(false);
            await Clients.Client(result.HostConnectionId)
                .SessionApproved(result.State)
                .ConfigureAwait(false);
            await NotifyAnnotationColorsAsync(result.SessionId).ConfigureAwait(false);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.AnnotatorApproved,
                "Annotator approved. SessionId={SessionId} AnnotatorClientInstanceId={ClientInstanceId}",
                result.SessionId,
                result.AnnotatorCredential.ClientInstanceId);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "ApproveAnnotator");
        }
    }

    public async Task RejectAnnotator(string sessionId, string annotatorConnectionId)
    {
        try
        {
            var result = sessionManager.RejectAnnotator(
                sessionId,
                annotatorConnectionId,
                Context.ConnectionId);
            await Clients.Client(result.AnnotatorConnectionId)
                .SessionEnded("Connection request declined by host.")
                .ConfigureAwait(false);
            await NotifyDirectoryChangedAsync().ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.AnnotatorJoinRejected,
                "Annotator join declined. SessionId={SessionId} AnnotatorConnectionId={AnnotatorConnectionId}",
                result.SessionId,
                result.AnnotatorConnectionId);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "RejectAnnotator");
        }
    }

    public async Task SetAnnotationColorPreference(string color)
    {
        try
        {
            var assignments = sessionManager.SetAnnotationColorPreference(
                Context.ConnectionId,
                color);
            await SendAnnotationColorsAsync(assignments).ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "SetAnnotationColorPreference");
        }
    }

    /// <summary>
    /// Reallocates a session's colours and tells whoever moved. Called after anything that
    /// changes who is in the session, so a departure frees the colour it was holding and hands it
    /// straight back to whoever wanted it first.
    /// </summary>
    private async Task NotifyAnnotationColorsAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        await SendAnnotationColorsAsync(sessionManager.RefreshAnnotationColors(sessionId))
            .ConfigureAwait(false);
    }

    private async Task SendAnnotationColorsAsync(
        IReadOnlyList<AnnotationColorAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            await Clients.Client(assignment.ConnectionId)
                .AnnotationColorAssigned(assignment.Color)
                .ConfigureAwait(false);
        }
    }

    public async Task SendPointer(PointerEventMessage pointerEvent)
    {
        try
        {
            var result = sessionManager.AcceptPointer(Context.ConnectionId, pointerEvent);
            if (result.Disposition == PointerRelayDisposition.Accepted
                && result.HostConnectionId is not null)
            {
                // Stamped here rather than trusted from the annotator: the host uses it to say
                // which of its annotators is drawing, and only the relay knows that for certain.
                await Clients.Client(result.HostConnectionId)
                    .PointerReceived(pointerEvent with { AnnotatorId = result.AnnotatorId })
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
            if (result.AnnotatorConnectionId is not null)
            {
                await Clients.Client(result.AnnotatorConnectionId)
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
            if (result.HostPreserved && result.State is not null)
            {
                var cancelledRequestConnectionId =
                    result.CancelledAnnotatorRequestConnectionId;
                if (cancelledRequestConnectionId is not null
                    && result.HostConnectionId is not null)
                {
                    await Clients.Client(result.HostConnectionId)
                        .AnnotatorJoinCancelled(cancelledRequestConnectionId)
                        .ConfigureAwait(false);
                }

                foreach (var annotatorConnectionId in GetAnnotatorConnectionIds(result))
                {
                    await Groups.RemoveFromGroupAsync(
                            annotatorConnectionId,
                            GroupName(sessionId))
                        .ConfigureAwait(false);
                    await Clients.Client(annotatorConnectionId)
                        .SessionEnded(
                            string.Equals(
                                annotatorConnectionId,
                                cancelledRequestConnectionId,
                                StringComparison.Ordinal)
                                ? "Connection request cancelled."
                                : "Disconnected from the host.")
                        .ConfigureAwait(false);
                }

                if (result.HostConnectionId is not null)
                {
                    await Clients.Client(result.HostConnectionId)
                        .SessionApproved(result.State)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await Clients.Group(GroupName(sessionId))
                    .SessionEnded("The host connection ended and is no longer available.")
                    .ConfigureAwait(false);
            }
            await NotifyAnnotationColorsAsync(result.SessionId).ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionEnded,
                "Connection ended. SessionId={SessionId} HostPreserved={HostPreserved} PointerCount={PointerCount}",
                result.SessionId,
                result.HostPreserved,
                result.PointerCount);
            await NotifyDirectoryChangedAsync(result.Room).ConfigureAwait(false);
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
            var result = sessionManager.DisconnectAnnotators(
                sessionId,
                Context.ConnectionId);
            foreach (var annotatorConnectionId in GetAnnotatorConnectionIds(result))
            {
                await Groups.RemoveFromGroupAsync(
                        annotatorConnectionId,
                        GroupName(sessionId))
                    .ConfigureAwait(false);
                await Clients.Client(annotatorConnectionId)
                    .SessionEnded("Disconnected by the host.")
                    .ConfigureAwait(false);
            }

            if (result.HostConnectionId is not null && result.State is not null)
            {
                await Clients.Client(result.HostConnectionId)
                    .SessionApproved(result.State)
                    .ConfigureAwait(false);
            }

            await NotifyAnnotationColorsAsync(result.SessionId).ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionEnded,
                "Host disconnected all annotators. SessionId={SessionId} PointerCount={PointerCount}",
                result.SessionId,
                result.PointerCount);
            await NotifyDirectoryChangedAsync(result.Room).ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "DisconnectAllConnections");
        }
    }

    public async Task DisconnectAnnotator(string sessionId, string annotatorId)
    {
        try
        {
            var result = sessionManager.DisconnectAnnotator(
                sessionId,
                Context.ConnectionId,
                annotatorId);
            foreach (var annotatorConnectionId in GetAnnotatorConnectionIds(result))
            {
                await Groups.RemoveFromGroupAsync(
                        annotatorConnectionId,
                        GroupName(sessionId))
                    .ConfigureAwait(false);
                await Clients.Client(annotatorConnectionId)
                    .SessionEnded("Disconnected by the host.")
                    .ConfigureAwait(false);
            }

            if (result.HostConnectionId is not null && result.State is not null)
            {
                await Clients.Client(result.HostConnectionId)
                    .SessionApproved(result.State)
                    .ConfigureAwait(false);
            }

            await NotifyAnnotationColorsAsync(result.SessionId).ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionEnded,
                "Host disconnected an annotator. SessionId={SessionId} AnnotatorClientInstanceId={ClientInstanceId}",
                result.SessionId,
                annotatorId);
            await NotifyDirectoryChangedAsync(result.Room).ConfigureAwait(false);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "DisconnectAnnotator");
        }
    }

    /// <summary>
    /// Pauses or resumes one annotator, or all of them when <paramref name="annotatorId"/> is
    /// null. The paused annotator is told as well, so it can show that its input is going
    /// nowhere instead of drawing into a stream the relay drops.
    /// </summary>
    public async Task SetAnnotatorPaused(string sessionId, string? annotatorId, bool paused)
    {
        try
        {
            var result = sessionManager.SetAnnotatorPaused(
                sessionId,
                Context.ConnectionId,
                annotatorId,
                paused);
            foreach (var annotatorConnectionId in result.AnnotatorConnectionIds)
            {
                await Clients.Client(annotatorConnectionId)
                    .AnnotationPaused(paused)
                    .ConfigureAwait(false);
            }

            if (result.HostConnectionId is not null)
            {
                await Clients.Client(result.HostConnectionId)
                    .SessionApproved(result.State)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                AuditEventIds.AnnotatorPauseChanged,
                "Host changed annotator pause state. SessionId={SessionId} AnnotatorClientInstanceId={ClientInstanceId} Paused={Paused}",
                result.SessionId,
                annotatorId ?? "*",
                paused);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "SetAnnotatorPaused");
        }
    }

    private async Task CancelJoinRequestAsync(SessionTerminationResult cancellation)
    {
        var annotatorConnectionId = cancellation.CancelledAnnotatorRequestConnectionId;
        if (annotatorConnectionId is null)
        {
            return;
        }

        await Clients.Client(annotatorConnectionId)
            .SessionEnded("The host moved to another room, so the connection request was cancelled.")
            .ConfigureAwait(false);
        if (cancellation.HostConnectionId is not null)
        {
            await Clients.Client(cancellation.HostConnectionId)
                .AnnotatorJoinCancelled(annotatorConnectionId)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            AuditEventIds.SessionEnded,
            "Join request cancelled across a room change. SessionId={SessionId} AnnotatorConnectionId={AnnotatorConnectionId}",
            cancellation.SessionId,
            annotatorConnectionId);
    }

    private HubException ToHubException(SessionOperationException exception, string operation)
    {
        LogValidationFailure(exception.Code, operation);
        return new HubException(exception.Message);
    }

    private static IReadOnlyList<string> GetAnnotatorConnectionIds(
        SessionTerminationResult result) =>
        result.AnnotatorConnectionIds
        ?? (result.AnnotatorConnectionId is null ? [] : [result.AnnotatorConnectionId]);

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

    internal static string GroupName(string sessionId) => $"session:{sessionId}";

    /// <summary>
    /// Directory changes reach only the clients in the same room, so one client's connection
    /// churn no longer costs a directory read on every other connection.
    /// </summary>
    internal static string DirectoryGroupName(string room) => $"directory:{room}";

    private Task NotifyDirectoryChangedAsync() =>
        NotifyDirectoryChangedAsync(sessionManager.GetConnectionRoom(Context.ConnectionId));

    private Task NotifyDirectoryChangedAsync(string room) =>
        Clients.Group(DirectoryGroupName(room)).HostDirectoryChanged();

    /// <summary>
    /// Notifies both directories a change touched, skipping the second when it is the same one.
    /// A connection normally ends in the room its session was published in, but an approved
    /// annotator that changed rooms does not, and the free slot it leaves behind belongs to the
    /// session's room rather than to the one it walked off with.
    /// </summary>
    private async Task NotifyDirectoryChangedAsync(string room, string? sessionRoom)
    {
        await NotifyDirectoryChangedAsync(room).ConfigureAwait(false);
        if (sessionRoom is not null
            && !string.Equals(sessionRoom, room, StringComparison.Ordinal))
        {
            await NotifyDirectoryChangedAsync(sessionRoom).ConfigureAwait(false);
        }
    }
}
