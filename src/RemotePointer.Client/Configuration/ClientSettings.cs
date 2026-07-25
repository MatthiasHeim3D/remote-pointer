using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
namespace RemotePointer.Client.Configuration;

public sealed class ClientSettings
{
    private string userPreferencesPath = GetDefaultUserPreferencesPath();

    public ServerSettings Server { get; init; } = new();

    public PointerSettings Pointer { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();

    public UserProfileSettings Profile { get; init; } = new();

    public HostSettings Host { get; init; } = new();

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
        int? maximumAnnotatorConnections = null,
        bool? launchAtStartup = null,
        string? selectedDisplayId = null,
        bool? showUsageHints = null,
        bool? hostAvailable = null,
        int? drawingOpacityPercent = null)
    {
        var normalizedServerAddress = NormalizeServerAddress(serverAddress);
        var normalizedUserName = userName.Trim();
        var requestedMaximumAnnotatorConnections = maximumAnnotatorConnections
            ?? Host.MaximumAnnotatorConnections;

        ValidateValues(
            normalizedServerAddress,
            normalizedUserName,
            requestedMaximumAnnotatorConnections);

        Server.BaseUrl = normalizedServerAddress;
        Profile.UserName = normalizedUserName;
        Profile.PicturePath = profilePicturePath?.Trim() ?? string.Empty;
        if (maximumAnnotatorConnections.HasValue)
        {
            Host.MaximumAnnotatorConnections = maximumAnnotatorConnections.Value;
        }
        if (launchAtStartup.HasValue)
        {
            Startup.LaunchAtStartup = launchAtStartup.Value;
        }
        Host.SelectedDisplayId = selectedDisplayId?.Trim() ?? string.Empty;
        if (showUsageHints.HasValue)
        {
            Pointer.ShowUsageHints = showUsageHints.Value;
        }
        if (hostAvailable.HasValue)
        {
            Host.IsAvailable = hostAvailable.Value;
        }
        if (drawingOpacityPercent.HasValue)
        {
            Pointer.DrawingOpacityPercent = PointerSettings.ClampDrawingOpacityPercent(
                drawingOpacityPercent.Value);
        }
        WriteUserPreferences();
    }

    public void SaveHostAvailability(bool isAvailable)
    {
        Host.IsAvailable = isAvailable;
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
                Host.MaximumAnnotatorConnections,
                Startup.LaunchAtStartup,
                Host.SelectedDisplayId,
                Pointer.ShowUsageHints,
                Host.IsAvailable,
                Pointer.HasShownUsageHints,
                Pointer.DrawingOpacityPercent),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });

        // Replacing the file atomically keeps an interrupted write from leaving truncated
        // JSON behind, which the next start would have to recover from.
        var temporaryPath = $"{userPreferencesPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, userPreferencesPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal void Validate()
    {
        if (Server.ReconnectDelaysSeconds.Length == 0
            || Server.ReconnectDelaysSeconds.Any(delay => delay < 0)
            || Pointer.DefaultTtlMilliseconds <= 0)
        {
            throw new InvalidOperationException("Client reconnect and pointer settings are invalid.");
        }

        Server.BaseUrl = NormalizeServerAddress(Server.BaseUrl);
        ValidateValues(Server.BaseUrl, Profile.UserName, Host.MaximumAnnotatorConnections);
    }

    public static bool TryNormalizeServerAddress(
        string? serverAddress,
        out string normalizedAddress,
        out string validationMessage)
    {
        normalizedAddress = serverAddress?.Trim() ?? string.Empty;
        validationMessage = string.Empty;
        if (normalizedAddress.Length == 0)
        {
            return true;
        }

        if (!Uri.TryCreate(normalizedAddress, UriKind.Absolute, out var serverUri)
            || !string.Equals(serverUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(serverUri.Host))
        {
            validationMessage = "Enter a valid HTTPS server address.";
            return false;
        }

        if (!string.IsNullOrEmpty(serverUri.UserInfo))
        {
            validationMessage = "The server address must not contain a username or password.";
            return false;
        }

        normalizedAddress = normalizedAddress.TrimEnd('/');
        return true;
    }

    private static string NormalizeServerAddress(string? serverAddress)
    {
        if (TryNormalizeServerAddress(
                serverAddress,
                out var normalizedAddress,
                out var validationMessage))
        {
            return normalizedAddress;
        }

        throw new InvalidOperationException(validationMessage);
    }

    private static void ValidateValues(
        string serverAddress,
        string userName,
        int maximumAnnotatorConnections)
    {
        _ = NormalizeServerAddress(serverAddress);

        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 128)
        {
            throw new InvalidOperationException("Username is required and must be 128 characters or fewer.");
        }

        if (maximumAnnotatorConnections is < 1 or > 16)
        {
            throw new InvalidOperationException(
                "Maximum connected annotators must be between 1 and 16.");
        }
    }

    private void ApplyUserPreferences(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        UserPreferences? preferences;
        try
        {
            preferences = JsonSerializer.Deserialize<UserPreferences>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception exception) when (
            exception is JsonException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Unreadable saved preferences must not make the client unlaunchable. Starting
            // from defaults puts the user back in first-run setup, which they can complete.
            return;
        }

        if (preferences is null)
        {
            return;
        }

        // Every stored value is treated as untrusted: a hand-edited or partially written file
        // must not produce a configuration that Validate rejects during startup.
        if (preferences.ServerAddress is not null
            && TryNormalizeServerAddress(preferences.ServerAddress, out var serverAddress, out _))
        {
            Server.BaseUrl = serverAddress;
        }

        Profile.UserName = IsSupportedUserName(preferences.UserName)
            ? preferences.UserName
            : string.Empty;
        Profile.PicturePath = preferences.ProfilePicturePath ?? string.Empty;
        var maximumAnnotatorConnections = preferences.EffectiveMaximumAnnotatorConnections;
        Host.MaximumAnnotatorConnections = Math.Clamp(
            maximumAnnotatorConnections <= 0 ? 2 : maximumAnnotatorConnections,
            1,
            16);
        Host.SelectedDisplayId = preferences.SelectedDisplayId ?? string.Empty;
        Host.IsAvailable = preferences.EffectiveHostAvailable;
        Startup.LaunchAtStartup = preferences.LaunchAtStartup;
        Pointer.ShowUsageHints = preferences.ShowUsageHints;
        Pointer.HasShownUsageHints = preferences.HasShownUsageHints;
        Pointer.DrawingOpacityPercent = PointerSettings.ClampDrawingOpacityPercent(
            preferences.DrawingOpacityPercent);
    }

    private static bool IsSupportedUserName(string? userName) =>
        !string.IsNullOrWhiteSpace(userName) && userName.Length <= 128;

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

    private static string GetDefaultUserPreferencesPath() =>
        ClientDataDirectory.Resolve("user-settings.json");

    private sealed record UserPreferences(
        string ServerAddress,
        string UserName,
        string ProfilePicturePath,
        int? MaximumAnnotatorConnections = null,
        bool LaunchAtStartup = false,
        string SelectedDisplayId = "",
        bool ShowUsageHints = true,
        bool? HostAvailable = null,
        bool HasShownUsageHints = false,
        int DrawingOpacityPercent = PointerSettings.DefaultDrawingOpacityPercent)
    {
        /// <summary>
        /// The annotator limit under the name it had before the sender/annotator rename. A file
        /// written by an older client still uses it, and without reading it here an upgrade
        /// would silently drop the user's choice back to the default.
        /// </summary>
        [JsonPropertyName("maximumSenderConnections")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaximumSenderConnections { get; init; }

        /// <summary>
        /// Host availability under the name it had before the receiver/host rename.
        /// </summary>
        [JsonPropertyName("receiverAvailable")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ReceiverAvailable { get; init; }

        // The current name wins when both are present, so the first save after an upgrade
        // settles the file on the new spelling and the legacy value stops being consulted.
        public int EffectiveMaximumAnnotatorConnections =>
            MaximumAnnotatorConnections ?? MaximumSenderConnections ?? 2;

        public bool EffectiveHostAvailable => HostAvailable ?? ReceiverAvailable ?? false;
    }
}

public sealed class ServerSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    public int[] ReconnectDelaysSeconds { get; init; } = [0, 2, 5, 10, 30];

    /// <summary>
    /// The key derived from the server password. It is loaded from and written to the protected
    /// store rather than the preferences file, so it is never serialised alongside the settings.
    /// </summary>
    public string? PasswordKey { get; set; }
}

public sealed class PointerSettings
{
    public const int MinimumDrawingOpacityPercent = 10;

    public const int MaximumDrawingOpacityPercent = 100;

    public const int DefaultDrawingOpacityPercent = 50;

    public int DefaultTtlMilliseconds { get; init; } = 2_000;

    public int AnimationMilliseconds { get; init; } = 900;

    public string ToggleHotKey { get; init; } = "Ctrl+Alt+P";

    public bool ShowUsageHints { get; set; } = true;

    public bool HasShownUsageHints { get; set; }

    /// <summary>
    /// How opaque the annotator's own shapes are drawn inside the input area. The host always
    /// renders at full opacity; this only softens the local copy so it competes less with the
    /// same drawing coming back through the shared video feed.
    /// </summary>
    public int DrawingOpacityPercent { get; set; } = DefaultDrawingOpacityPercent;

    public static int ClampDrawingOpacityPercent(int percent) => Math.Clamp(
        percent <= 0 ? DefaultDrawingOpacityPercent : percent,
        MinimumDrawingOpacityPercent,
        MaximumDrawingOpacityPercent);
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

public sealed class HostSettings
{
    public int MaximumAnnotatorConnections { get; set; } = 2;

    public string SelectedDisplayId { get; set; } = string.Empty;

    public bool IsAvailable { get; set; }
}

public sealed class StartupSettings
{
    public bool LaunchAtStartup { get; set; }
}
