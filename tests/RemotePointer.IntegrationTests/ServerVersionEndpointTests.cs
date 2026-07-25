using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.IntegrationTests;

public sealed class ServerVersionEndpointTests
{
    [Fact]
    public async Task VersionEndpoint_AdvertisesTheBuildVersion()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/version", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var payload = await response.Content.ReadFromJsonAsync<ServerVersionResponse>();
        Assert.NotNull(payload);

        // The client uses the product id to tell the relay apart from an unrelated server that
        // also answers /health and /version.
        Assert.Equal(ServerVersionResponse.RelayProductId, payload.Product);
        Assert.False(string.IsNullOrWhiteSpace(payload.Version));

        // The commit metadata Nerdbank.GitVersioning appends is stripped before it is advertised.
        Assert.DoesNotContain("+", payload.Version, StringComparison.Ordinal);
    }
}
