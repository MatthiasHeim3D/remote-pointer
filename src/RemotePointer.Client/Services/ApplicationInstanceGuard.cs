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
    private EventWaitHandle? activationEvent;
    private RegisteredWaitHandle? activationRegistration;

    private ApplicationInstanceGuard(Mutex mutex, EventWaitHandle activationEvent)
    {
        this.mutex = mutex;
        this.activationEvent = activationEvent;
    }

    internal static bool TryAcquire(
        string mutexName,
        string activationEventName,
        bool enforceSingleInstance,
        out ApplicationInstanceGuard? guard)
    {
        guard = null;
        if (!enforceSingleInstance)
        {
            return true;
        }

        var candidateActivationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            activationEventName);
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
            candidateActivationEvent.Set();
            candidateActivationEvent.Dispose();
            candidate.Dispose();
            return false;
        }

        guard = new ApplicationInstanceGuard(candidate, candidateActivationEvent);
        return true;
    }

    internal void ListenForActivation(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(activationEvent is null, this);
        if (activationRegistration is not null)
        {
            throw new InvalidOperationException("The activation listener is already registered.");
        }

        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                {
                    ((Action)state!).Invoke();
                }
            },
            activationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (mutex is null)
        {
            return;
        }

        activationRegistration?.Unregister(null);
        activationRegistration = null;
        activationEvent?.Dispose();
        activationEvent = null;
        mutex.ReleaseMutex();
        mutex.Dispose();
        mutex = null;
    }
}
