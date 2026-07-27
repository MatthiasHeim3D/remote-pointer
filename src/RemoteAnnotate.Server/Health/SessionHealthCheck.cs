using Microsoft.Extensions.Diagnostics.HealthChecks;
using RemoteAnnotate.Server.Sessions;

namespace RemoteAnnotate.Server.Health;

public sealed class SessionHealthCheck(ISessionManager sessionManager) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return Task.FromResult(
            HealthCheckResult.Healthy(
                "The in-memory session manager is available.",
                new Dictionary<string, object>
                {
                    ["activeSessions"] = sessionManager.ActiveSessionCount,
                }));
    }
}
