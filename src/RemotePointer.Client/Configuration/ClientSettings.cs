using System.Text.Json;
using System.IO;

namespace RemotePointer.Client.Configuration;

public sealed class ClientSettings
{
    public ServerSettings Server { get; init; } = new();

    public PointerSettings Pointer { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();

    public static ClientSettings Load(string? baseDirectory = null)
    {
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var path = Path.Combine(directory, "appsettings.json");
        ClientSettings settings;
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<ClientSettings>(
                    json,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new ClientSettings();
        }
        else
        {
            settings = new ClientSettings();
        }

        var environmentUrl = Environment.GetEnvironmentVariable("REMOTEPOINTER_SERVER_BASEURL");
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            settings.Server.BaseUrl = environmentUrl;
        }

        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        if (!Uri.TryCreate(Server.BaseUrl, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Server BaseUrl must use HTTPS.");
        }

        if (Server.ReconnectDelaysSeconds.Length == 0
            || Server.ReconnectDelaysSeconds.Any(delay => delay < 0)
            || Pointer.DefaultTtlMilliseconds <= 0)
        {
            throw new InvalidOperationException("Client reconnect and pointer settings are invalid.");
        }
    }
}

public sealed class ServerSettings
{
    public string BaseUrl { get; set; } = "https://localhost:7243";

    public int[] ReconnectDelaysSeconds { get; init; } = [0, 2, 5, 10, 30];
}

public sealed class PointerSettings
{
    public int DefaultTtlMilliseconds { get; init; } = 2_000;

    public int AnimationMilliseconds { get; init; } = 900;

    public string ToggleHotKey { get; init; } = "Ctrl+Alt+P";
}

public sealed class PrivacySettings
{
    public bool LogCoordinates { get; init; }
}
