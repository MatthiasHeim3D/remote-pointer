using Microsoft.AspNetCore.SignalR;
using RemotePointer.Server.Hubs;
using RemotePointer.Server.Security;

namespace RemotePointer.Server.Sessions;

public sealed class SessionExpirationService(
    ISessionManager sessionManager,
    IHubContext<PointerHub, IPointerClient> hubContext,
    ILogger<SessionExpirationService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var session in sessionManager.CollectExpiredSessions())
            {
                await hubContext.Clients.Group($"session:{session.SessionId}")
                    .SessionEnded("The session expired.")
                    .ConfigureAwait(false);
                logger.LogInformation(
                    AuditEventIds.SessionExpired,
                    "Session expired. SessionId={SessionId} PointerCount={PointerCount}",
                    session.SessionId,
                    session.PointerCount);
            }
        }
    }
}
