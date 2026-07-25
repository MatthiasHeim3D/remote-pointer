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
        Assert.Equal(2, settings.Receiver.MaximumSenderConnections);
    }

    [Fact]
    public void Load_AcceptsEmptyServerForInitialSetup()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, string.Empty);

        var settings = ClientSettings.Load(directory.Path, null);

        Assert.Empty(settings.Server.BaseUrl);
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
    public void Load_UsesWindowsAccountDefaultsForUnsetProfile()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var picture = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lXfJYQAAAABJRU5ErkJggg==");

        var settings = ClientSettings.Load(
            directory.Path,
            null,
            new FixedProfileDefaultsProvider("Heim, Matthias", picture));

        Assert.Equal("Heim, Matthias", settings.Profile.UserName);
        Assert.StartsWith(directory.Path, settings.Profile.PicturePath, StringComparison.Ordinal);
        Assert.EndsWith(".png", settings.Profile.PicturePath, StringComparison.Ordinal);
        Assert.NotEmpty(File.ReadAllBytes(settings.Profile.PicturePath));
    }

    [Fact]
    public void Load_PreservesConfiguredProfileInsteadOfWindowsAccountDefaults()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);
        settings.SaveUserPreferences(
            "https://packaged.example.test",
            "Configured Name",
            @"C:\Pictures\configured.png");

        var reloaded = ClientSettings.Load(
            directory.Path,
            null,
            new FixedProfileDefaultsProvider("Windows Name", [1, 2, 3]));

        Assert.Equal("Configured Name", reloaded.Profile.UserName);
        Assert.Equal(@"C:\Pictures\configured.png", reloaded.Profile.PicturePath);
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
            @"C:\Pictures\ada.png",
            maximumSenderConnections: 4,
            launchAtStartup: true,
            selectedDisplayId: "display-2",
            showUsageHints: false,
            receiverAvailable: true);
        var reloaded = ClientSettings.Load(directory.Path, null);

        Assert.Equal("https://saved.example.test", reloaded.Server.BaseUrl);
        Assert.Equal("Ada Lovelace", reloaded.Profile.UserName);
        Assert.Equal(@"C:\Pictures\ada.png", reloaded.Profile.PicturePath);
        Assert.Equal(4, reloaded.Receiver.MaximumSenderConnections);
        Assert.True(reloaded.Startup.LaunchAtStartup);
        Assert.Equal("display-2", reloaded.Receiver.SelectedDisplayId);
        Assert.False(reloaded.Pointer.ShowUsageHints);
        Assert.True(reloaded.Receiver.IsAvailable);
        Assert.False(reloaded.Pointer.HasShownUsageHints);
    }

    [Fact]
    public void Load_RecoversFromTruncatedUserPreferences()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        File.WriteAllText(
            System.IO.Path.Combine(directory.Path, "user-settings.json"),
            "{\"serverAddress\":\"https://saved.exa");

        var settings = ClientSettings.Load(directory.Path, null);

        Assert.Equal("https://packaged.example.test", settings.Server.BaseUrl);
        Assert.Equal(Environment.UserName, settings.Profile.UserName);
        Assert.Equal(2, settings.Receiver.MaximumSenderConnections);
    }

    [Fact]
    public void Load_IgnoresStoredValuesThatWouldFailValidation()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        WriteUserPreferences(
            directory.Path,
            new
            {
                serverAddress = "http://insecure.example.test",
                userName = new string('n', 200),
                profilePicturePath = string.Empty,
                maximumSenderConnections = 99,
            });

        var settings = ClientSettings.Load(directory.Path, null);

        Assert.Equal("https://packaged.example.test", settings.Server.BaseUrl);
        Assert.Equal(Environment.UserName, settings.Profile.UserName);
        Assert.Equal(16, settings.Receiver.MaximumSenderConnections);
    }

    [Fact]
    public void SaveUserPreferences_LeavesNoPartialFileBehind()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        settings.SaveUserPreferences("https://saved.example.test", "Ada", null);
        settings.SaveUserPreferences("https://saved.example.test", "Ada Lovelace", null);

        Assert.Equal(
            ["appsettings.json", "user-settings.json"],
            Directory.GetFiles(directory.Path)
                .Select(file => System.IO.Path.GetFileName(file) ?? string.Empty)
                .Order(StringComparer.Ordinal)
                .ToArray());
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
    public void SaveUserPreferences_InvalidServerDoesNotMutateCurrentSettings()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://original.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            settings.SaveUserPreferences("not a valid URL", "Ada", null));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://original.example.test", settings.Server.BaseUrl);
    }

    [Fact]
    public void SaveUserPreferences_AllowsClearingServerAddress()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://original.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        settings.SaveUserPreferences(string.Empty, "Ada", null);
        var reloaded = ClientSettings.Load(directory.Path, null);

        Assert.Empty(reloaded.Server.BaseUrl);
    }

    [Fact]
    public void SaveReceiverAvailability_PersistsSelectionImmediately()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        settings.SaveReceiverAvailability(true);
        var reloaded = ClientSettings.Load(directory.Path, null);

        Assert.True(reloaded.Receiver.IsAvailable);
    }

    [Fact]
    public void UserPreferences_RoundTripDrawingOpacity()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        settings.SaveUserPreferences(
            "https://saved.example.test",
            "Ada",
            null,
            drawingOpacityPercent: 25);
        var reloaded = ClientSettings.Load(directory.Path, null);

        Assert.Equal(25, reloaded.Pointer.DrawingOpacityPercent);
    }

    [Fact]
    public void Load_DefaultsDrawingOpacityWhenPreferencesPredateTheSetting()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        WriteUserPreferences(
            directory.Path,
            new
            {
                serverAddress = "https://saved.example.test",
                userName = "Ada",
                profilePicturePath = string.Empty,
            });

        var settings = ClientSettings.Load(directory.Path, null);

        Assert.Equal(
            PointerSettings.DefaultDrawingOpacityPercent,
            settings.Pointer.DrawingOpacityPercent);
    }

    [Theory]
    [InlineData(0, PointerSettings.DefaultDrawingOpacityPercent)]
    [InlineData(-40, PointerSettings.DefaultDrawingOpacityPercent)]
    [InlineData(3, PointerSettings.MinimumDrawingOpacityPercent)]
    [InlineData(400, PointerSettings.MaximumDrawingOpacityPercent)]
    public void Load_ClampsStoredDrawingOpacityIntoSupportedRange(
        int storedPercent,
        int expectedPercent)
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        WriteUserPreferences(
            directory.Path,
            new
            {
                serverAddress = "https://saved.example.test",
                userName = "Ada",
                profilePicturePath = string.Empty,
                drawingOpacityPercent = storedPercent,
            });

        var settings = ClientSettings.Load(directory.Path, null);

        Assert.Equal(expectedPercent, settings.Pointer.DrawingOpacityPercent);
    }

    [Fact]
    public void SaveUsageHintsShown_PersistsFirstUseState()
    {
        using var directory = new TemporaryDirectory();
        WriteSettings(directory.Path, "https://packaged.example.test");
        var settings = ClientSettings.Load(directory.Path, null);

        settings.SaveUsageHintsShown();
        var reloaded = ClientSettings.Load(directory.Path, null);

        Assert.True(reloaded.Pointer.HasShownUsageHints);
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

    private static void WriteUserPreferences(string directory, object preferences) =>
        File.WriteAllText(
            System.IO.Path.Combine(directory, "user-settings.json"),
            JsonSerializer.Serialize(preferences));

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

    private sealed class FixedProfileDefaultsProvider(string userName, byte[]? picture)
        : IUserProfileDefaultsProvider
    {
        public UserProfileDefaults GetCurrentProfile() => new(userName, picture);
    }
}
