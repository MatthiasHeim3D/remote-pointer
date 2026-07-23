using Microsoft.AspNetCore.SignalR;
using RemotePointer.Contracts.Messages;
using RemotePointer.Server.Sessions;
using RemotePointer.Server.Security;

namespace RemotePointer.Server.Hubs;

public sealed class PointerHub(
    ISessionManager sessionManager,
    ILogger<PointerHub> logger) : Hub<IPointerClient>
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation(
            AuditEventIds.ClientConnected,
            "Relay client connected. ConnectionId={ConnectionId} ClientInstanceId={ClientInstanceId}",
            Context.ConnectionId,
            GetOptionalQueryValue("clientInstanceId"));
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        sessionManager.Disconnect(Context.ConnectionId);
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

    public async Task<CreateSessionResponse> CreateReceiverSession(DisplayDescriptor display)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        try
        {
            var response = sessionManager.CreateReceiverSession(
                display,
                Context.ConnectionId,
                clientInstanceId);
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
            return response;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "CreateReceiverSession");
        }
    }

    public async Task<JoinResponse> RequestToJoinSession(JoinRequest request)
    {
        var clientInstanceId = GetRequiredClientInstanceId();
        if (!string.Equals(
                request.ClientInstanceId,
                clientInstanceId,
                StringComparison.Ordinal))
        {
            LogValidationFailure("client_identity_mismatch", "RequestToJoinSession");
            return new JoinResponse(false, null, "The join request identity is invalid.");
        }

        try
        {
            var result = sessionManager.RequestToJoinSession(
                request,
                Context.ConnectionId,
                GetDisplayName(clientInstanceId));
            if (result.Response.Accepted
                && result.ReceiverConnectionId is not null
                && result.Presenter is not null)
            {
                await Clients.Client(result.ReceiverConnectionId)
                    .PresenterJoinRequested(result.Presenter)
                    .ConfigureAwait(false);
                logger.LogInformation(
                    AuditEventIds.PresenterJoinRequested,
                    "Presenter join requested. SessionId={SessionId} PresenterClientInstanceId={ClientInstanceId}",
                    result.Response.SessionId,
                    request.ClientInstanceId);
            }
            else
            {
                logger.LogWarning(
                    AuditEventIds.PresenterJoinRejected,
                    "Presenter join rejected. ClientInstanceId={ClientInstanceId} Reason={Reason}",
                    request.ClientInstanceId,
                    result.Response.Reason);
            }

            return result.Response;
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "RequestToJoinSession");
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
            var result = sessionManager.ResumeSession(Context.ConnectionId, request);
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
            await Clients.Group(GroupName(sessionId))
                .SessionEnded("The session was ended by a participant.")
                .ConfigureAwait(false);
            logger.LogInformation(
                AuditEventIds.SessionEnded,
                "Session ended. SessionId={SessionId} PointerCount={PointerCount}",
                result.SessionId,
                result.PointerCount);
        }
        catch (SessionOperationException exception)
        {
            throw ToHubException(exception, "EndSession");
        }
    }

    private HubException ToHubException(SessionOperationException exception, string operation)
    {
        LogValidationFailure(exception.Code, operation);
        return new HubException(exception.Message);
    }

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
}
