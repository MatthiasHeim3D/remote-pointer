using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RemotePointer.Contracts.Messages;

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
    private const int MaximumVersionLength = 40;

    // Deliberately lenient: a newer relay may add fields, and an unreadable version must never
    // turn a reachable server into a failed test.
    private static readonly JsonSerializerOptions VersionSerializerOptions =
        new(JsonSerializerDefaults.Web);

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
                var serverVersion = await ReadServerVersionAsync(serverAddress, timeout.Token)
                    .ConfigureAwait(false);
                return new ServerConnectionTestResult(
                    true,
                    "Connection successful.",
                    serverVersion);
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

    /// <summary>
    /// Reads the advertised server version. Every failure maps to null: the version is a label,
    /// so a relay that predates the endpoint stays fully usable.
    /// </summary>
    private static async Task<string?> ReadServerVersionAsync(
        string serverAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            var versionAddress = $"{serverAddress.TrimEnd('/')}/version";
            using var response = await HttpClient
                .GetAsync(versionAddress, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<ServerVersionResponse>(
                    VersionSerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return Sanitize(payload?.Version);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or JsonException
            or NotSupportedException
            or OperationCanceledException
            or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The version is server-controlled text rendered in the settings pane, so it is trimmed,
    /// stripped of control characters and capped before it reaches the view.
    /// </summary>
    private static string? Sanitize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var sanitized = new string(
            version.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (sanitized.Length == 0)
        {
            return null;
        }

        return sanitized.Length <= MaximumVersionLength
            ? sanitized
            : sanitized[..MaximumVersionLength];
    }
}
