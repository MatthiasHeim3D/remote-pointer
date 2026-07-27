using System.IO;
using System.Text.Json;
using RemoteAnnotate.Client.Configuration;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Services;

public sealed class JsonFileClientAuditLog : IClientAuditLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string logDirectory;
    private readonly Lock syncRoot = new();
    private readonly TimeProvider timeProvider;

    public JsonFileClientAuditLog(string? logDirectory = null, TimeProvider? timeProvider = null)
    {
        this.logDirectory = logDirectory ?? ClientDataDirectory.Resolve("Logs");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Write(
        ClientAuditEvent auditEvent,
        ClientAuditLevel level = ClientAuditLevel.Information,
        string? sessionId = null,
        ClientRole? role = null,
        RelayConnectionStatus? connectionStatus = null,
        Exception? exception = null)
    {
        try
        {
            lock (syncRoot)
            {
                var now = timeProvider.GetUtcNow();
                var record = new ClientAuditRecord(
                    now,
                    auditEvent.ToString(),
                    level.ToString(),
                    sessionId,
                    role?.ToString(),
                    connectionStatus?.ToString(),
                    exception?.GetType().FullName,
                    exception?.HResult);
                Directory.CreateDirectory(logDirectory);
                var path = Path.Combine(logDirectory, $"audit-{now:yyyyMMdd}.jsonl");
                File.AppendAllText(
                    path,
                    JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine);
            }
        }
        catch (Exception loggingException) when (
            loggingException is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Audit logging must never destabilize the pointer application. No user data,
            // exception message, pointer coordinate, or credential is sent to a fallback sink.
        }
    }

    private sealed record ClientAuditRecord(
        DateTimeOffset Timestamp,
        string Event,
        string Level,
        string? SessionId,
        string? Role,
        string? ConnectionStatus,
        string? ErrorType,
        int? ErrorCode);
}
