using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows.Media.Imaging;
namespace RemotePointer.Client.Configuration;

public sealed class ClientSettings
{
    private string userPreferencesPath = GetDefaultUserPreferencesPath();

    public ServerSettings Server { get; init; } = new();

    public PointerSettings Pointer { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();

    public UserProfileSettings Profile { get; init; } = new();

    public ReceiverSettings Receiver { get; init; } = new();

    public StartupSettings Startup { get; init; } = new();

    public static ClientSettings Load(string? baseDirectory = null)
    {
        return Load(
            baseDirectory,
            Environment.GetEnvironmentVariable("REMOTEPOINTER_SERVER_BASEURL"),
            baseDirectory is null ? new WindowsAccountProfileDefaultsProvider() : null);
    }

    internal static ClientSettings Load(
        string? baseDirectory,
        string? environmentUrl) => Load(baseDirectory, environmentUrl, null);

    internal static ClientSettings Load(
        string? baseDirectory,
        string? environmentUrl,
        IUserProfileDefaultsProvider? profileDefaultsProvider)
    {
        var directory = baseDirectory ?? AppContext.BaseDirectory;
        var path = Path.Combine(directory, "appsettings.json");
        var userPreferencesPath = baseDirectory is null
            ? GetDefaultUserPreferencesPath()
            : Path.Combine(baseDirectory, "user-settings.json");
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

        settings.userPreferencesPath = userPreferencesPath;
        settings.ApplyUserPreferences(userPreferencesPath);
        settings.ApplyMissingProfileDefaults(profileDefaultsProvider);

        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            settings.Server.BaseUrl = environmentUrl;
        }

        settings.Validate();
        return settings;
    }

    public void SaveUserPreferences(
        string serverAddress,
        string userName,
        string? profilePicturePath,
        int? maximumSenderConnections = null,
        bool? launchAtStartup = null,
        string? selectedDisplayId = null,
        bool? showUsageHints = null,
        bool? receiverAvailable = null)
    {
        Server.BaseUrl = serverAddress.Trim();
        Profile.UserName = userName.Trim();
        Profile.PicturePath = profilePicturePath?.Trim() ?? string.Empty;
        if (maximumSenderConnections.HasValue)
        {
            Receiver.MaximumSenderConnections = maximumSenderConnections.Value;
        }
        if (launchAtStartup.HasValue)
        {
            Startup.LaunchAtStartup = launchAtStartup.Value;
        }
        Receiver.SelectedDisplayId = selectedDisplayId?.Trim() ?? string.Empty;
        if (showUsageHints.HasValue)
        {
            Pointer.ShowUsageHints = showUsageHints.Value;
        }
        if (receiverAvailable.HasValue)
        {
            Receiver.IsAvailable = receiverAvailable.Value;
        }
        Validate();

        WriteUserPreferences();
    }

    public void SaveReceiverAvailability(bool isAvailable)
    {
        Receiver.IsAvailable = isAvailable;
        WriteUserPreferences();
    }

    public void SaveUsageHintsShown()
    {
        Pointer.HasShownUsageHints = true;
        WriteUserPreferences();
    }

    private void WriteUserPreferences()
    {
        var parentDirectory = Path.GetDirectoryName(userPreferencesPath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        var json = JsonSerializer.Serialize(
            new UserPreferences(
                Server.BaseUrl,
                Profile.UserName,
                Profile.PicturePath,
                Receiver.MaximumSenderConnections,
                Startup.LaunchAtStartup,
                Receiver.SelectedDisplayId,
                Pointer.ShowUsageHints,
                Receiver.IsAvailable,
                Pointer.HasShownUsageHints),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });
        File.WriteAllText(userPreferencesPath, json);
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

        if (string.IsNullOrWhiteSpace(Profile.UserName) || Profile.UserName.Length > 128)
        {
            throw new InvalidOperationException("Username is required and must be 128 characters or fewer.");
        }

        if (Receiver.MaximumSenderConnections is < 1 or > 16)
        {
            throw new InvalidOperationException(
                "Maximum connected senders must be between 1 and 16.");
        }
    }

    private void ApplyUserPreferences(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var preferences = JsonSerializer.Deserialize<UserPreferences>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (preferences is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferences.ServerAddress))
        {
            Server.BaseUrl = preferences.ServerAddress;
        }

        Profile.UserName = preferences.UserName ?? string.Empty;
        Profile.PicturePath = preferences.ProfilePicturePath ?? string.Empty;
        Receiver.MaximumSenderConnections = preferences.MaximumSenderConnections <= 0
            ? 2
            : preferences.MaximumSenderConnections;
        Receiver.SelectedDisplayId = preferences.SelectedDisplayId ?? string.Empty;
        Receiver.IsAvailable = preferences.ReceiverAvailable;
        Startup.LaunchAtStartup = preferences.LaunchAtStartup;
        Pointer.ShowUsageHints = preferences.ShowUsageHints;
        Pointer.HasShownUsageHints = preferences.HasShownUsageHints;
    }

    private void ApplyMissingProfileDefaults(IUserProfileDefaultsProvider? provider)
    {
        if (!string.IsNullOrWhiteSpace(Profile.UserName)
            && !string.IsNullOrWhiteSpace(Profile.PicturePath))
        {
            return;
        }

        var defaults = provider?.GetCurrentProfile()
            ?? new UserProfileDefaults(Environment.UserName, null);
        if (string.IsNullOrWhiteSpace(Profile.UserName))
        {
            Profile.UserName = string.IsNullOrWhiteSpace(defaults.UserName)
                ? Environment.UserName
                : defaults.UserName.Trim();
        }

        if (string.IsNullOrWhiteSpace(Profile.PicturePath)
            && defaults.Picture is { Length: > 0 })
        {
            Profile.PicturePath = TryCacheDefaultProfilePicture(defaults.Picture) ?? string.Empty;
        }
    }

    private string? TryCacheDefaultProfilePicture(byte[] picture)
    {
        try
        {
            var directory = Path.GetDirectoryName(userPreferencesPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            Directory.CreateDirectory(directory);
            using var sourceStream = new MemoryStream(picture, writable: false);
            var decoder = BitmapDecoder.Create(
                sourceStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var cachedPicture = new MemoryStream();
            encoder.Save(cachedPicture);

            var path = Path.Combine(directory, "windows-account-picture.png");
            File.WriteAllBytes(path, cachedPicture.ToArray());
            return path;
        }
        catch (Exception exception) when (
            exception is IOException
                or ArgumentException
                or FormatException
                or InvalidOperationException
                or UnauthorizedAccessException
                or SecurityException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static string GetDefaultUserPreferencesPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemotePointer",
        "user-settings.json");

    private sealed record UserPreferences(
        string ServerAddress,
        string UserName,
        string ProfilePicturePath,
        int MaximumSenderConnections = 2,
        bool LaunchAtStartup = false,
        string SelectedDisplayId = "",
        bool ShowUsageHints = true,
        bool ReceiverAvailable = false,
        bool HasShownUsageHints = false);
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

    public bool ShowUsageHints { get; set; } = true;

    public bool HasShownUsageHints { get; set; }
}

public sealed class PrivacySettings
{
    public bool LogCoordinates { get; init; }
}

public sealed class UserProfileSettings
{
    public string UserName { get; set; } = string.Empty;

    public string PicturePath { get; set; } = string.Empty;
}

public sealed class ReceiverSettings
{
    public int MaximumSenderConnections { get; set; } = 2;

    public string SelectedDisplayId { get; set; } = string.Empty;

    public bool IsAvailable { get; set; }
}

public sealed class StartupSettings
{
    public bool LaunchAtStartup { get; set; }
}
