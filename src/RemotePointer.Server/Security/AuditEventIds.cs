using Microsoft.Extensions.Logging;

namespace RemotePointer.Server.Security;

public static class AuditEventIds
{
    public static readonly EventId ClientConnected = new(1000, nameof(ClientConnected));
    public static readonly EventId ClientDisconnected = new(1001, nameof(ClientDisconnected));
    public static readonly EventId PlaintextRejected = new(1002, nameof(PlaintextRejected));
    public static readonly EventId SessionCreated = new(1100, nameof(SessionCreated));
    public static readonly EventId PresenterJoinRequested = new(1101, nameof(PresenterJoinRequested));
    public static readonly EventId PresenterJoinRejected = new(1102, nameof(PresenterJoinRejected));
    public static readonly EventId PresenterApproved = new(1103, nameof(PresenterApproved));
    public static readonly EventId SessionResumed = new(1104, nameof(SessionResumed));
    public static readonly EventId SessionEnded = new(1105, nameof(SessionEnded));
    public static readonly EventId SessionExpired = new(1106, nameof(SessionExpired));
    public static readonly EventId OperationRejected = new(1200, nameof(OperationRejected));
    public static readonly EventId UnhandledHubError = new(1900, nameof(UnhandledHubError));
}
