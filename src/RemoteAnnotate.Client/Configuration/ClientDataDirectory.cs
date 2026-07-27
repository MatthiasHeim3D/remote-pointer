using System.IO;

namespace RemoteAnnotate.Client.Configuration;

/// <summary>
/// Resolves the per-user directory holding everything this client owns: preferences, its durable
/// client identity, protected credentials, calibrations, and audit logs.
/// </summary>
/// <remarks>
/// <see cref="OverrideVariableName"/> redirects the whole tree, which is what lets several clients
/// run side by side under one Windows account with separate identities instead of fighting over
/// one set of files. It is read once per process at startup, so a client keeps the directory it
/// started with. DPAPI is scoped to the Windows account rather than to the path, so a redirected
/// client still protects and reads its own credentials normally.
/// </remarks>
public static class ClientDataDirectory
{
    public const string OverrideVariableName = "REMOTEANNOTATE_DATA_DIRECTORY";

    private static readonly string RootDirectory = ResolveRoot();

    /// <summary>
    /// Combines <paramref name="segments"/> onto the resolved root. Passing none returns the root.
    /// </summary>
    public static string Resolve(params string[] segments) =>
        segments is { Length: > 0 }
            ? Path.Combine([RootDirectory, .. segments])
            : RootDirectory;

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(OverrideVariableName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteAnnotate");
        }

        // A relative override would otherwise follow the working directory, which the tray icon
        // and the installed shortcut do not agree on.
        return Path.GetFullPath(configured.Trim());
    }
}
