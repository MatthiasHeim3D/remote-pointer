using System.Net;
using System.Net.Http;

namespace RemotePointer.Client.Services;

public sealed class ServerConnectionTester : IServerConnectionTester
{
    // The handler is shared for the lifetime of the process, so connections are recycled to
    // pick up DNS changes for a relay host that moves.
    private static readonly HttpClient HttpClient = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    public async Task<ServerConnectionTestResult> TestAsync(
        string serverAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            var healthAddress = $"{serverAddress.TrimEnd('/')}/health";
            using var response = await HttpClient.GetAsync(
                    healthAddress,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new ServerConnectionTestResult(true, "Connection successful.");
            }

            return new ServerConnectionTestResult(
                false,
                $"The server returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServerConnectionTestResult(false, "The connection test timed out.");
        }
        catch (HttpRequestException exception)
        {
            var message = exception.StatusCode is HttpStatusCode statusCode
                ? $"The server returned HTTP {(int)statusCode} ({statusCode})."
                : "The server could not be reached.";
            return new ServerConnectionTestResult(false, message);
        }
        catch (UriFormatException)
        {
            return new ServerConnectionTestResult(false, "The server address is invalid.");
        }
    }
}
