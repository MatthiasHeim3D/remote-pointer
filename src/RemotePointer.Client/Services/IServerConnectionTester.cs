namespace RemotePointer.Client.Services;

public interface IServerConnectionTester
{
    Task<ServerConnectionTestResult> TestAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a connection test. A successful result always carries the
/// <paramref name="ServerVersion"/> the relay advertised: identifying itself as a Remote Pointer
/// relay is part of passing the test, so a server that cannot is reported as a failure.
/// </summary>
public sealed record ServerConnectionTestResult(
    bool IsSuccessful,
    string Message,
    string? ServerVersion = null);
