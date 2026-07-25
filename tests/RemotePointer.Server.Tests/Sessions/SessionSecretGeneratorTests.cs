using RemotePointer.Server.Sessions;

namespace RemotePointer.Server.Tests.Sessions;

public sealed class SessionSecretGeneratorTests
{
    private readonly SessionSecretGenerator generator = new();

    [Fact]
    public void HashSecret_DoesNotContainPlaintextAndMatchesInConstantTimePath()
    {
        var secret = generator.GenerateSecret();
        var hash = generator.HashSecret(secret);

        Assert.DoesNotContain(secret, hash, StringComparison.Ordinal);
        Assert.True(generator.SecretMatches(secret, hash));
        Assert.False(generator.SecretMatches(generator.GenerateSecret(), hash));
    }

    [Fact]
    public void GenerateSecret_HasAtLeast256BitsOfRandomInput()
    {
        var secret = generator.GenerateSecret();

        Assert.True(secret.Length >= 43);
    }
}
