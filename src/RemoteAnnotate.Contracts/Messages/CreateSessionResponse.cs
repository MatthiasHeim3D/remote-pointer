namespace RemoteAnnotate.Contracts.Messages;

public sealed record CreateSessionResponse(
    string SessionId,
    string SessionSecret,
    SessionCredential Credential);
