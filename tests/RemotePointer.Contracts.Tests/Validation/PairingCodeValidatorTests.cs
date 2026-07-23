using RemotePointer.Contracts.Validation;

namespace RemotePointer.Contracts.Tests.Validation;

public sealed class PairingCodeValidatorTests
{
    [Theory]
    [InlineData("AB2D4E")]
    [InlineData("ab2d4e")]
    [InlineData("AB2-D4E")]
    [InlineData(" AB2 D4E ")]
    public void IsValid_AcceptsFriendlyFormatting(string value)
    {
        Assert.True(PairingCodeValidator.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDE")]
    [InlineData("ABCDEFG")]
    [InlineData("AB0D4E")]
    [InlineData("ABI D4E")]
    [InlineData("AB2_D4E")]
    public void IsValid_RejectsInvalidCodes(string? value)
    {
        Assert.False(PairingCodeValidator.IsValid(value));
    }

    [Fact]
    public void Normalize_RemovesSeparatorsAndUsesUppercase()
    {
        Assert.Equal("AB2D4E", PairingCodeValidator.Normalize(" ab2-d4e "));
    }
}
