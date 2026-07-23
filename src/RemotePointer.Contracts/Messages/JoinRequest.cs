namespace RemotePointer.Contracts.Messages;

public sealed record JoinRequest(
    string PairingCode,
    ClientRole Role,
    string ClientInstanceId,
    string ClientVersion);
