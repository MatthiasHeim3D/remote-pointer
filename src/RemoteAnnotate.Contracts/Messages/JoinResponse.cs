namespace RemoteAnnotate.Contracts.Messages;

public sealed record JoinResponse(
    bool Accepted,
    string? SessionId,
    string? Reason);
