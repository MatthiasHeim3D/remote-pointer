namespace RemoteAnnotate.Contracts.Messages;

/// <summary>
/// The payload served by the relay under <c>/version</c>. <see cref="Product"/> identifies the
/// service so a client can tell a Remote Annotate relay apart from an unrelated server that
/// happens to answer the same well-known paths; <see cref="Version"/> is the relay build.
/// </summary>
public sealed record ServerVersionResponse(string Product, string Version)
{
    /// <summary>
    /// The value <see cref="Product"/> carries. Clients reject a server that reports anything else.
    /// </summary>
    public const string RelayProductId = "remote-annotate-relay";
}
