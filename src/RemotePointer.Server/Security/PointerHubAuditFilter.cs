using Microsoft.AspNetCore.SignalR;

namespace RemotePointer.Server.Security;

public sealed class PointerHubAuditFilter(ILogger<PointerHubAuditFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                AuditEventIds.UnhandledHubError,
                "Unhandled hub operation failure. Operation={Operation} ConnectionId={ConnectionId} ErrorType={ErrorType} ErrorCode={ErrorCode}",
                invocationContext.HubMethodName,
                invocationContext.Context.ConnectionId,
                exception.GetType().FullName,
                exception.HResult);
            throw;
        }
    }
}
