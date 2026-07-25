using System.IO;
using System.Security.Cryptography;
using System.Text;
using RemotePointer.Client.Configuration;

namespace RemotePointer.Client.Services;

/// <summary>
/// Holds the group key derived from the server password under DPAPI, next to the session
/// credentials. Only the derived key is stored — never the password — so the file cannot give
/// back a secret the user may have reused elsewhere, and the settings screen has nothing to
/// echo back.
/// </summary>
public sealed class ProtectedServerPasswordStore : IServerPasswordStore
{
    private readonly IDataProtector dataProtector;
    private readonly IClientAuditLog? auditLog;
    private readonly string filePath;
    private readonly Lock syncRoot = new();

    public ProtectedServerPasswordStore(
        IDataProtector dataProtector,
        IClientAuditLog? auditLog = null,
        string? directoryPath = null)
    {
        this.dataProtector = dataProtector ?? throw new ArgumentNullException(nameof(dataProtector));
        this.auditLog = auditLog;
        filePath = Path.Combine(
            directoryPath ?? ClientDataDirectory.Resolve("Sessions"),
            "server-password.key");
    }

    public string? Load()
    {
        lock (syncRoot)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            byte[]? plaintext = null;
            try
            {
                plaintext = dataProtector.Unprotect(File.ReadAllBytes(filePath));
                var key = Encoding.UTF8.GetString(plaintext);
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or CryptographicException
                    or NotSupportedException)
            {
                ClearNoLock();
                auditLog?.Write(
                    ClientAuditEvent.SessionCredentialProtectionFailed,
                    ClientAuditLevel.Warning,
                    exception: exception);
                return null;
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
    }

    public void Save(string groupKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
        lock (syncRoot)
        {
            var plaintext = Encoding.UTF8.GetBytes(groupKey);
            try
            {
                var protectedBytes = dataProtector.Protect(plaintext);
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllBytes(temporaryPath, protectedBytes);
                    File.Move(temporaryPath, filePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            ClearNoLock();
        }
    }

    private void ClearNoLock()
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            auditLog?.Write(
                ClientAuditEvent.SessionCredentialProtectionFailed,
                ClientAuditLevel.Warning,
                exception: exception);
        }
    }
}
