using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class ApplicationInstanceGuardTests
{
    [Fact]
    public void TryAcquire_WhenNotEnforced_AllowsMultipleInstances()
    {
        var mutexName = CreateMutexName();
        var activationEventName = CreateActivationEventName();

        Assert.True(ApplicationInstanceGuard.TryAcquire(
            mutexName,
            activationEventName,
            false,
            out var first));
        Assert.True(ApplicationInstanceGuard.TryAcquire(
            mutexName,
            activationEventName,
            false,
            out var second));
        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_WhenEnforced_AllowsOnlyOneInstanceAtATime()
    {
        var mutexName = CreateMutexName();
        var activationEventName = CreateActivationEventName();
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        using var activationReceived = new ManualResetEventSlim();
        var ownerAcquired = false;
        Exception? ownerException = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                ownerAcquired = ApplicationInstanceGuard.TryAcquire(
                    mutexName,
                    activationEventName,
                    true,
                    out var ownerGuard);
                ownerGuard?.ListenForActivation(activationReceived.Set);
                ownerReady.Set();
                releaseOwner.Wait();
                ownerGuard?.Dispose();
            }
            catch (Exception exception)
            {
                ownerException = exception;
                ownerReady.Set();
            }
        });
        ownerThread.Start();
        try
        {
            Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(ownerException);
            Assert.True(ownerAcquired);
            Assert.False(ApplicationInstanceGuard.TryAcquire(
                mutexName,
                activationEventName,
                true,
                out var second));
            Assert.Null(second);
            Assert.True(activationReceived.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseOwner.Set();
            Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.True(ApplicationInstanceGuard.TryAcquire(
            mutexName,
            activationEventName,
            true,
            out var replacement));
        replacement?.Dispose();
    }

    [Fact]
    public void BuildPolicy_MatchesBuildConfiguration()
    {
#if DEBUG
        Assert.False(ApplicationInstancePolicy.EnforceSingleInstance);
#else
        Assert.True(ApplicationInstancePolicy.EnforceSingleInstance);
#endif
    }

    private static string CreateMutexName() =>
        $"RemotePointer.Client.Tests.{Guid.NewGuid():N}";

    private static string CreateActivationEventName() =>
        $"RemotePointer.Client.Tests.Activate.{Guid.NewGuid():N}";
}
