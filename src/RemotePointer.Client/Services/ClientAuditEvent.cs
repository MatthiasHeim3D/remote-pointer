namespace RemotePointer.Client.Services;

public enum ClientAuditEvent
{
    ApplicationStarted,
    ApplicationStopped,
    ConfigurationRejected,
    ConnectionStateChanged,
    SessionCredentialProtected,
    SessionCredentialProtectionFailed,
    SessionRestored,
    SessionRestoreFailed,
    SessionEnded,
    UnhandledException,
}

public enum ClientAuditLevel
{
    Information,
    Warning,
    Error,
}
