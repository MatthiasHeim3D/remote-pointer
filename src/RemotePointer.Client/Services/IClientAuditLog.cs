using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public interface IClientAuditLog
{
    void Write(
        ClientAuditEvent auditEvent,
        ClientAuditLevel level = ClientAuditLevel.Information,
        string? sessionId = null,
        ClientRole? role = null,
        RelayConnectionStatus? connectionStatus = null,
        Exception? exception = null);
}
