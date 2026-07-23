using RemotePointer.Contracts.Validation;
using RemotePointer.Server.Sessions;

namespace RemotePointer.Server.Tests.Sessions;

public sealed class SessionSecretGeneratorTests
{
    private readonly SessionSecretGenerator generator = new();

    [Fact]
    public void GeneratePairingCode_UsesFriendlyValidAlphabet()
    {
        var codes = Enumerable.Range(0, 100)
            .Select(_ => generator.GeneratePairingCode())
            .ToArray();

        Assert.All(codes, code => Assert.True(PairingCodeValidator.IsValid(code)));
        Assert.True(codes.Distinct(StringComparer.Ordinal).Count() > 95);
    }

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
