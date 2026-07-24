using System.Text.Json;
using System.Net.Http;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Configuration;

public sealed class ClientSettingsTests
{
    [Fact]
    public void Load_RejectsPlaintextLoopbackServer()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "http://localhost:5243");

        var exception = Assert.Throws<InvalidOperationException>(
            () => ClientSettings.Load(directory.Path, null));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsHttpsServer()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://pointer.example.test");

        var settings = ClientSettings.Load(directory.Path, null);

        Assert.Equal("https://pointer.example.test", settings.Server.BaseUrl);
    }

    [Fact]
    public void Load_EnvironmentServerUrlOverridesPackagedSettings()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(
            directory.Path,
            "https://environment.example.test");

        Assert.Equal("https://environment.example.test", settings.Server.BaseUrl);
    }

    [Fact]
    public void UserPreferences_RoundTripServerAndProfile()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        settings.SaveUserPreferences(
            "https://saved.example.test",
            "Ada Lovelace",
            @"C:\Pictures\ada.png");
        var reloaded = ClientSettings.Load(directory.Path, null);

        Assert.Equal("https://saved.example.test", reloaded.Server.BaseUrl);
        Assert.Equal("Ada Lovelace", reloaded.Profile.UserName);
        Assert.Equal(@"C:\Pictures\ada.png", reloaded.Profile.PicturePath);
    }

    [Fact]
    public void SaveUserPreferences_RejectsEmptyUsername()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            settings.SaveUserPreferences("https://saved.example.test", " ", null));

        Assert.Contains("Username", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelayClient_PublicApi_ExposesNoHttpHandlerOrCertificateBypassHook()
    {
        var publicConstructorParameters = typeof(SignalRRelayClient)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(
            publicConstructorParameters,
            type => type == typeof(Func<HttpMessageHandler>)
                || typeof(HttpMessageHandler).IsAssignableFrom(type));
    }

    private static void WriteSettings(string directory, string baseUrl)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                Server = new
                {
                    BaseUrl = baseUrl,
                    ReconnectDelaysSeconds = new[] { 0, 2 },
                },
                Pointer = new
                {
                    DefaultTtlMilliseconds = 2_000,
                },
            });
        File.WriteAllText(System.IO.Path.Combine(directory, "appsettings.json"), json);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RemotePointer.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
