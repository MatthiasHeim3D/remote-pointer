namespace RemoteAnnotate.Client.Services;

internal static class RelayClientShutdown
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Completes a relay client's asynchronous teardown from a synchronous disposal path.
    /// The window's Closed handler cannot await, so the work runs on the thread pool - where
    /// it cannot resume onto the blocked dispatcher - and is bounded, so an unreachable relay
    /// delays shutdown instead of preventing it.
    /// </summary>
    internal static void Complete(IRelayClient relayClient)
    {
        ArgumentNullException.ThrowIfNull(relayClient);
        try
        {
            _ = Task.Run(async () => await relayClient.DisposeAsync().ConfigureAwait(false))
                .Wait(Timeout);
        }
        catch (AggregateException)
        {
            // The relay reports its own failures through the audit log, and a teardown error
            // must not stop the application from closing.
        }
    }
}
