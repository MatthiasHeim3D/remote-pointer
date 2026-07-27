using RemoteAnnotate.Contracts.Messages;
using RemoteAnnotate.Contracts.Validation;

namespace RemoteAnnotate.Contracts.Tests.Validation;

public sealed class ContractValidatorTests
{
    [Fact]
    public void Validate_AcceptsValidDisplay()
    {
        var display = new DisplayDescriptor("display-1", "Display 1", 1_920, 1_080, 1.5d, 0);

        Assert.True(ContractValidator.Validate(display).IsValid);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    public void Validate_RejectsUnsupportedDisplayRotation(int rotation)
    {
        var display = new DisplayDescriptor("display-1", "Display 1", 1_920, 1_080, 1d, rotation);

        Assert.False(ContractValidator.Validate(display).IsValid);
    }

    [Fact]
    public void Validate_RejectsInvalidDisplayDimensionsAndScale()
    {
        var display = new DisplayDescriptor("", "", 0, -1, double.NaN, 0);

        var result = ContractValidator.Validate(display);

        Assert.False(result.IsValid);
        Assert.Equal(5, result.Errors.Count);
    }

    [Fact]
    public void Validate_ClientProfileAcceptsBoundedPngAndRejectsOtherPayloads()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.True(ContractValidator.Validate(new ClientProfile(png)).IsValid);
        Assert.False(ContractValidator.Validate(new ClientProfile([1, 2, 3])).IsValid);
        Assert.False(ContractValidator.Validate(
            new ClientProfile(new byte[ContractValidator.MaximumProfilePictureBytes + 1])).IsValid);
    }

    [Fact]
    public void Validate_AcceptsValidDirectJoinRequest()
    {
        var request = new DirectJoinRequest("session-id", "client-id", "1.0.0");

        Assert.True(ContractValidator.Validate(request).IsValid);
    }

    [Fact]
    public void Validate_RejectsInvalidDirectJoinRequest()
    {
        var request = new DirectJoinRequest("", "", "");

        var result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void Validate_ValidatesNestedHostDisplay()
    {
        var display = new DisplayDescriptor("display", "Display", -1, 1_080, 1d, 0);
        var state = new SessionStateMessage("session", true, display, DateTimeOffset.UtcNow.AddHours(1));

        Assert.False(ContractValidator.Validate(state).IsValid);
    }
}
