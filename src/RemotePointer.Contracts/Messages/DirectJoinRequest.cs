namespace RemotePointer.Contracts.Messages;

public sealed record DirectJoinRequest(
    string SessionId,
    string ClientInstanceId,
    string ClientVersion);
