using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public sealed class ProtectedSessionStore : IProtectedSessionStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IClientAuditLog? auditLog;
    private readonly IDataProtector dataProtector;
    private readonly string sessionDirectory;
    private readonly Lock syncRoot = new();
    private readonly TimeProvider timeProvider;

    public ProtectedSessionStore(
        IDataProtector dataProtector,
        IClientAuditLog? auditLog = null,
        string? sessionDirectory = null,
        TimeProvider? timeProvider = null)
    {
        this.dataProtector = dataProtector ?? throw new ArgumentNullException(nameof(dataProtector));
        this.auditLog = auditLog;
        this.sessionDirectory = sessionDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemotePointer",
            "Sessions");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SessionCredential? Load(ClientRole role, string clientInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        lock (syncRoot)
        {
            var path = GetPath(role);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var protectedBytes = File.ReadAllBytes(path);
                var plaintext = dataProtector.Unprotect(protectedBytes);
                try
                {
                    var document = JsonSerializer.Deserialize<ProtectedSessionDocument>(
                        plaintext,
                        SerializerOptions);
                    if (document is null
                        || document.Version != CurrentVersion
                        || document.Credential.Role != role
                        || !string.Equals(
                            document.Credential.ClientInstanceId,
                            clientInstanceId,
                            StringComparison.Ordinal)
                        || document.Credential.ExpiresAt <= timeProvider.GetUtcNow())
                    {
                        ClearNoLock(role);
                        return null;
                    }

                    return document.Credential;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or CryptographicException
                    or JsonException
                    or NotSupportedException)
            {
                ClearNoLock(role);
                auditLog?.Write(
                    ClientAuditEvent.SessionRestoreFailed,
                    ClientAuditLevel.Warning,
                    role: role,
                    exception: exception);
                return null;
            }
        }
    }

    public void Save(SessionCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (syncRoot)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new ProtectedSessionDocument(CurrentVersion, credential),
                SerializerOptions);
            try
            {
                var protectedBytes = dataProtector.Protect(plaintext);
                Directory.CreateDirectory(sessionDirectory);
                var path = GetPath(credential.Role);
                var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllBytes(temporaryPath, protectedBytes);
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }

                auditLog?.Write(
                    ClientAuditEvent.SessionCredentialProtected,
                    sessionId: credential.SessionId,
                    role: credential.Role);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Clear(ClientRole role)
    {
        lock (syncRoot)
        {
            ClearNoLock(role);
        }
    }

    private string GetPath(ClientRole role) => Path.Combine(
        sessionDirectory,
        role == ClientRole.Receiver ? "receiver.session" : "presenter.session");

    private void ClearNoLock(ClientRole role)
    {
        var path = GetPath(role);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            auditLog?.Write(
                ClientAuditEvent.SessionCredentialProtectionFailed,
                ClientAuditLevel.Warning,
                role: role,
                exception: exception);
        }
    }

    private sealed record ProtectedSessionDocument(int Version, SessionCredential Credential);
}
