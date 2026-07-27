using System.Text;
using RemoteAnnotate.Client.Services;
using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Client.Tests.Services;

public sealed class ProtectedSessionStoreTests
{
    [Fact]
    public void SaveAndLoad_ProtectsCredentialAtRest()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProtectedSessionStore(
            new ReversingDataProtector(),
            sessionDirectory: directory.Path);
        var credential = CreateCredential(DateTimeOffset.UtcNow.AddHours(1));

        store.Save(credential);
        var rawFile = File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Path)));
        var loaded = store.Load(ClientRole.Annotator, "annotator-client");

        Assert.Equal(credential, loaded);
        Assert.DoesNotContain(credential.SessionToken, rawFile, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.ReconnectToken, rawFile, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DiscardsExpiredOrWrongIdentityCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProtectedSessionStore(
            new ReversingDataProtector(),
            sessionDirectory: directory.Path);
        store.Save(CreateCredential(DateTimeOffset.UtcNow.AddHours(1)));

        var loaded = store.Load(ClientRole.Annotator, "different-client");

        Assert.Null(loaded);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Load_DiscardsExpiredCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProtectedSessionStore(
            new ReversingDataProtector(),
            sessionDirectory: directory.Path);
        store.Save(CreateCredential(DateTimeOffset.UtcNow.AddMinutes(-1)));

        var loaded = store.Load(ClientRole.Annotator, "annotator-client");

        Assert.Null(loaded);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Load_DiscardsCorruptedProtectedState()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProtectedSessionStore(
            new ReversingDataProtector(),
            sessionDirectory: directory.Path);
        store.Save(CreateCredential(DateTimeOffset.UtcNow.AddHours(1)));
        File.WriteAllBytes(Assert.Single(Directory.GetFiles(directory.Path)), [0, 1, 2]);

        var loaded = store.Load(ClientRole.Annotator, "annotator-client");

        Assert.Null(loaded);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    private static SessionCredential CreateCredential(DateTimeOffset expiresAt) => new(
        "session-1",
        ClientRole.Annotator,
        "annotator-client",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq",
        "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijk",
        expiresAt);

    private sealed class ReversingDataProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext.Reverse()];

        public byte[] Unprotect(byte[] protectedData) => [.. protectedData.Reverse()];
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RemoteAnnotate.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
