namespace RemoteAnnotate.Server.Tests.Sessions;

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    internal void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}
