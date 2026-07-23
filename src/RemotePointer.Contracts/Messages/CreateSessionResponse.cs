namespace RemotePointer.Contracts.Messages;

public sealed record CreateSessionResponse(
    string SessionId,
    string PairingCode,
    string SessionSecret,
    SessionCredential Credential,
    DateTimeOffset PairingCodeExpiresAt);
