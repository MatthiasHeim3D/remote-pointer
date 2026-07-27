using Microsoft.Win32;
using System.Security;

namespace RemoteAnnotate.Client.Services;

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string ApplicationName = "RemoteAnnotate";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ApplicationName) is string value
                    && !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry could not be opened.");
        if (!enabled)
        {
            key.DeleteValue(ApplicationName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        key.SetValue(ApplicationName, $"\"{executablePath}\"", RegistryValueKind.String);
    }
}
