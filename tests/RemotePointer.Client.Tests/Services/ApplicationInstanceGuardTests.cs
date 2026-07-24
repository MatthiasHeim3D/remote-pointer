using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class ApplicationInstanceGuardTests
{
    [Fact]
    public void TryAcquire_WhenNotEnforced_AllowsMultipleInstances()
    {
        var mutexName = CreateMutexName();

        Assert.True(ApplicationInstanceGuard.TryAcquire(mutexName, false, out var first));
        Assert.True(ApplicationInstanceGuard.TryAcquire(mutexName, false, out var second));
        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_WhenEnforced_AllowsOnlyOneInstanceAtATime()
    {
        var mutexName = CreateMutexName();
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        var ownerAcquired = false;
        Exception? ownerException = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                ownerAcquired = ApplicationInstanceGuard.TryAcquire(
                    mutexName,
                    true,
                    out var ownerGuard);
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
            Assert.False(ApplicationInstanceGuard.TryAcquire(mutexName, true, out var second));
            Assert.Null(second);
        }
        finally
        {
            releaseOwner.Set();
            Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.True(ApplicationInstanceGuard.TryAcquire(mutexName, true, out var replacement));
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
}
