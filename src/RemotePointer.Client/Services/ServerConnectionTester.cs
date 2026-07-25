using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public sealed class ServerConnectionTester : IServerConnectionTester
{
    // The handler is shared for the lifetime of the process, so connections are recycled to
    // pick up DNS changes for a relay host that moves.
    private static readonly HttpClient SharedHttpClient = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumVersionLength = 40;

    // A stranger's endpoint may answer with anything at all, so the identity payload is read from
    // a bounded buffer instead of streaming whatever the host decides to send.
    private const int MaximumVersionPayloadBytes = 4 * 1024;

    // A newer relay may add fields to the payload, so unknown members are ignored rather than
    // treated as a mismatch.
    private static readonly JsonSerializerOptions VersionSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private const string NotARelayMessage =
        "The address answered, but it is not a Remote Pointer server.";

    private readonly HttpClient httpClient;

    public ServerConnectionTester()
        : this(SharedHttpClient)
    {
    }

    internal ServerConnectionTester(HttpClient httpClient) => this.httpClient = httpClient;

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
            using var response = await httpClient.GetAsync(
                    healthAddress,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                // Reachability alone proves nothing: /health is a common path. The relay is only
                // accepted once it identifies itself as one.
                var serverVersion = await ReadServerVersionAsync(serverAddress, timeout.Token)
                    .ConfigureAwait(false);
                return serverVersion is null
                    ? new ServerConnectionTestResult(false, NotARelayMessage)
                    : new ServerConnectionTestResult(
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
    /// Reads the version a Remote Pointer relay advertises, or null when the host is something
    /// else. This doubles as the identity check, so anything unexpected — a missing endpoint, a
    /// non-JSON body, a foreign or absent product id — fails the whole connection test.
    /// </summary>
    private async Task<string?> ReadServerVersionAsync(
        string serverAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            var versionAddress = $"{serverAddress.TrimEnd('/')}/version";
            using var response = await httpClient
                .GetAsync(versionAddress, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not "application/json")
            {
                return null;
            }

            var body = await ReadBoundedBodyAsync(response, cancellationToken)
                .ConfigureAwait(false);
            if (body is null)
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<ServerVersionResponse>(
                body,
                VersionSerializerOptions);
            if (payload is null
                || !string.Equals(
                    payload.Product,
                    ServerVersionResponse.RelayProductId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            return Sanitize(payload.Version);
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
    /// Reads at most <see cref="MaximumVersionPayloadBytes"/> from the response, returning null
    /// when the body is larger. The identity payload is tiny, so an oversized one is a mismatch.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumVersionPayloadBytes)
        {
            return null;
        }

        using var content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await content
                   .ReadAsync(chunk.AsMemory(), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumVersionPayloadBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
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
