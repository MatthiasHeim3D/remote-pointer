namespace RemoteAnnotate.Contracts.Messages;

public sealed record SessionResumeRequest(
    string SessionId,
    ClientRole Role,
    string ClientInstanceId,
    string SessionToken,
    string ReconnectToken);
