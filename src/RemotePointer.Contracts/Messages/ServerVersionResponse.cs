namespace RemotePointer.Contracts.Messages;

/// <summary>
/// The payload served by the relay under <c>/version</c>. <see cref="Product"/> identifies the
/// service so a client can tell a Remote Pointer relay apart from an unrelated host that happens
/// to answer the same well-known paths; <see cref="Version"/> is the relay build.
/// </summary>
public sealed record ServerVersionResponse(string Product, string Version)
{
    /// <summary>
    /// The value <see cref="Product"/> carries. Clients reject a server that reports anything else.
    /// </summary>
    public const string RelayProductId = "remote-pointer-relay";
}
