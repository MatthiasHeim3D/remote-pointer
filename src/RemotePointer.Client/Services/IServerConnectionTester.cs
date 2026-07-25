namespace RemotePointer.Client.Services;

public interface IServerConnectionTester
{
    Task<ServerConnectionTestResult> TestAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a reachability check. <paramref name="ServerVersion"/> is null when the server
/// does not advertise a version, which is the case for relays older than this feature.
/// </summary>
public sealed record ServerConnectionTestResult(
    bool IsSuccessful,
    string Message,
    string? ServerVersion = null);
