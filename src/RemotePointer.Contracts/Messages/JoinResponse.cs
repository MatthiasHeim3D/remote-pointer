namespace RemotePointer.Contracts.Messages;

public sealed record JoinResponse(
    bool Accepted,
    string? SessionId,
    string? Reason);
