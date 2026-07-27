namespace RemoteAnnotate.Contracts.Messages;

public sealed record PointerAcknowledgement(
    Guid EventId,
    long DisplayedAtUnixMilliseconds);
