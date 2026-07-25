using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class ServerPasswordTests : IDisposable
{
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"RemotePointer.PasswordTests.{Guid.NewGuid():N}");

    [Fact]
    public void Derive_IsStableForTheSamePasswordAndDiffersForOthers()
    {
        var first = ServerPasswordKey.Derive("correct horse battery");
        var second = ServerPasswordKey.Derive("correct horse battery");
        var other = ServerPasswordKey.Derive("correct horse batterz");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Derive_IgnoresSurroundingWhitespaceAndHidesThePassword()
    {
        const string password = "shared team password";

        var key = ServerPasswordKey.Derive($"  {password}  ");

        Assert.Equal(ServerPasswordKey.Derive(password), key);
        Assert.DoesNotContain(password, key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("=", key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short12")]
    public void IsValidPassword_RejectsAnythingTooShortToBeWorthSharing(string? password)
    {
        Assert.False(ServerPasswordKey.IsValidPassword(password));
    }

    [Fact]
    public void Derive_RejectsAPasswordBelowTheMinimumLength()
    {
        Assert.Throws<ArgumentException>(() => ServerPasswordKey.Derive("short12"));
    }

    [Fact]
    public void ProtectedStore_RoundTripsTheKeyAndClearsIt()
    {
        var store = new ProtectedServerPasswordStore(
            new DpapiDataProtector(),
            directoryPath: directoryPath);
        var key = ServerPasswordKey.Derive("shared team password");

        Assert.Null(store.Load());
        store.Save(key);
        Assert.Equal(key, store.Load());

        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void ProtectedStore_KeepsTheKeyOutOfThePlaintextFile()
    {
        var store = new ProtectedServerPasswordStore(
            new DpapiDataProtector(),
            directoryPath: directoryPath);
        var key = ServerPasswordKey.Derive("shared team password");

        store.Save(key);

        var stored = File.ReadAllBytes(Path.Combine(directoryPath, "server-password.key"));
        Assert.DoesNotContain(
            System.Text.Encoding.UTF8.GetString(stored),
            key,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedStore_DiscardsAFileItCannotUnprotect()
    {
        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, "server-password.key");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        var store = new ProtectedServerPasswordStore(
            new DpapiDataProtector(),
            directoryPath: directoryPath);

        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
