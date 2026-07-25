using System.Net;
using System.Net.Http;
using System.Text;
using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class ServerConnectionTesterTests
{
    private const string RelayPayload =
        """{"product":"remote-pointer-relay","version":"1.2.3"}""";

    [Fact]
    public async Task TestAsync_RelayIsAcceptedAndReportsItsVersion()
    {
        using var tester = CreateTester(RelayPayload);

        var result = await tester.Tester.TestAsync("https://relay.example.test");

        Assert.True(result.IsSuccessful);
        Assert.Equal("1.2.3", result.ServerVersion);
    }

    [Fact]
    public async Task TestAsync_HealthyHostWithoutVersionEndpointIsRejected()
    {
        using var tester = CreateTester(versionPayload: null);

        var result = await tester.Tester.TestAsync("https://stranger.example.test");

        Assert.False(result.IsSuccessful);
        Assert.Null(result.ServerVersion);
        Assert.Contains("not a Remote Pointer server", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAsync_ForeignProductIsRejected()
    {
        using var tester = CreateTester("""{"product":"something-else","version":"9.9.9"}""");

        var result = await tester.Tester.TestAsync("https://stranger.example.test");

        Assert.False(result.IsSuccessful);
        Assert.Null(result.ServerVersion);
    }

    [Fact]
    public async Task TestAsync_NonJsonVersionResponseIsRejected()
    {
        using var tester = CreateTester("<html>OK</html>", versionMediaType: "text/html");

        var result = await tester.Tester.TestAsync("https://stranger.example.test");

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task TestAsync_OversizedVersionResponseIsRejected()
    {
        var padding = new string('x', 8 * 1024);
        using var tester = CreateTester(
            $$"""{"product":"remote-pointer-relay","version":"1.2.3","padding":"{{padding}}"}""");

        var result = await tester.Tester.TestAsync("https://stranger.example.test");

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task TestAsync_UnhealthyHostIsRejectedWithoutIdentityCheck()
    {
        using var tester = CreateTester(RelayPayload, healthStatusCode: HttpStatusCode.ServiceUnavailable);

        var result = await tester.Tester.TestAsync("https://relay.example.test");

        Assert.False(result.IsSuccessful);
        Assert.Contains("503", result.Message, StringComparison.Ordinal);
    }

    private static TesterScope CreateTester(
        string? versionPayload,
        string versionMediaType = "application/json",
        HttpStatusCode healthStatusCode = HttpStatusCode.OK) =>
        new(new StubHandler(versionPayload, versionMediaType, healthStatusCode));

    private sealed class TesterScope : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly StubHandler handler;

        public TesterScope(StubHandler handler)
        {
            this.handler = handler;
            httpClient = new HttpClient(handler);
            Tester = new ServerConnectionTester(httpClient);
        }

        public ServerConnectionTester Tester { get; }

        public void Dispose()
        {
            httpClient.Dispose();
            handler.Dispose();
        }
    }

    private sealed class StubHandler(
        string? versionPayload,
        string versionMediaType,
        HttpStatusCode healthStatusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (string.Equals(path, "/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(healthStatusCode));
            }

            if (string.Equals(path, "/version", StringComparison.Ordinal)
                && versionPayload is not null)
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            versionPayload,
                            Encoding.UTF8,
                            versionMediaType),
                    });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
