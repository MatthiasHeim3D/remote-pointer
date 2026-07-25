namespace RemotePointer.Contracts.Messages;

public sealed record CreateSessionResponse(
    string SessionId,
    string SessionSecret,
    SessionCredential Credential);
