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
            () => ClientSettings.Load(directory.Path, MachineSettingsPath(directory.Path), null));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsHttpsServer()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://pointer.example.test");

        var settings = ClientSettings.Load(directory.Path, MachineSettingsPath(directory.Path), null);

        Assert.Equal("https://pointer.example.test", settings.Server.BaseUrl);
    }

    [Fact]
    public void Load_MachineSettingsOverridePackagedServerUrl()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        File.WriteAllText(
            MachineSettingsPath(directory.Path),
            """
            {
              "Server": {
                "BaseUrl": "https://machine.example.test"
              }
            }
            """);

        var settings = ClientSettings.Load(directory.Path, MachineSettingsPath(directory.Path), null);

        Assert.Equal("https://machine.example.test", settings.Server.BaseUrl);
    }

    [Fact]
    public void Load_RejectsPlaintextMachineServerUrl()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        File.WriteAllText(
            MachineSettingsPath(directory.Path),
            """{"Server":{"BaseUrl":"http://machine.example.test"}}""");

        var exception = Assert.Throws<InvalidOperationException>(
            () => ClientSettings.Load(directory.Path, MachineSettingsPath(directory.Path), null));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_EnvironmentServerUrlOverridesMachineSettings()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        File.WriteAllText(
            MachineSettingsPath(directory.Path),
            """{"Server":{"BaseUrl":"https://machine.example.test"}}""");

        var settings = ClientSettings.Load(
            directory.Path,
            MachineSettingsPath(directory.Path),
            "https://environment.example.test");

        Assert.Equal("https://environment.example.test", settings.Server.BaseUrl);
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

    private static string MachineSettingsPath(string directory) =>
        System.IO.Path.Combine(directory, "clientsettings.json");

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
