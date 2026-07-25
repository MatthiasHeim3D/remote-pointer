namespace RemotePointer.Contracts.Messages;

/// <summary>
/// The payload served by the relay under <c>/version</c> so clients can show which server build
/// they are talking to.
/// </summary>
public sealed record ServerVersionResponse(string Version);
