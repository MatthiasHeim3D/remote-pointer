namespace RemotePointer.Client.Services;

internal static class ApplicationInstancePolicy
{
#if DEBUG
    internal const bool EnforceSingleInstance = false;
#else
    internal const bool EnforceSingleInstance = true;
#endif
}

internal sealed class ApplicationInstanceGuard : IDisposable
{
    private Mutex? mutex;

    private ApplicationInstanceGuard(Mutex mutex)
    {
        this.mutex = mutex;
    }

    internal static bool TryAcquire(
        string mutexName,
        bool enforceSingleInstance,
        out ApplicationInstanceGuard? guard)
    {
        guard = null;
        if (!enforceSingleInstance)
        {
            return true;
        }

        var candidate = new Mutex(initiallyOwned: false, mutexName);
        bool acquired;
        try
        {
            acquired = candidate.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            candidate.Dispose();
            return false;
        }

        guard = new ApplicationInstanceGuard(candidate);
        return true;
    }

    public void Dispose()
    {
        if (mutex is null)
        {
            return;
        }

        mutex.ReleaseMutex();
        mutex.Dispose();
        mutex = null;
    }
}
