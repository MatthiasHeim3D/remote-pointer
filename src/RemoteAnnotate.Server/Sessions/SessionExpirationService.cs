using Microsoft.AspNetCore.SignalR;
using RemoteAnnotate.Server.Hubs;
using RemoteAnnotate.Server.Security;

namespace RemoteAnnotate.Server.Sessions;

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
                await hubContext.Clients.Group(PointerHub.GroupName(session.SessionId))
                    .SessionEnded("The session expired.")
                    .ConfigureAwait(false);
                // A collected session leaves the directory without any client having asked for
                // it, so the peers that were listing it are told to read it again. Nothing else
                // would tell them, and they would go on offering a host that cannot be
                // joined until something unrelated changed.
                await hubContext.Clients.Group(PointerHub.DirectoryGroupName(session.Room))
                    .HostDirectoryChanged()
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
