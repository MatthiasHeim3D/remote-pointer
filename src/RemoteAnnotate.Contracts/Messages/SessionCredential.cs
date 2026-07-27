namespace RemoteAnnotate.Contracts.Messages;

public sealed record SessionCredential(
    string SessionId,
    ClientRole Role,
    string ClientInstanceId,
    string SessionToken,
    string ReconnectToken,
    DateTimeOffset ExpiresAt);
