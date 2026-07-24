namespace RemotePointer.Client.Services;

public interface IServerConnectionTester
{
    Task<ServerConnectionTestResult> TestAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);
}

public sealed record ServerConnectionTestResult(bool IsSuccessful, string Message);
