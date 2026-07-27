namespace RemotePointer.Client.Services;

public enum RelayConnectionStatus
{
    Disconnected,
    Connected,
    Reconnecting,
    SessionExpired,

    /// <summary>
    /// The relay is reachable but refused this client's server password. Kept apart from
    /// <see cref="Disconnected"/> because the address is right and only the password is wrong,
    /// which is a different thing to tell the user and cannot be retried out of.
    /// </summary>
    Unauthorized,
}
