using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Validation;

namespace RemotePointer.Contracts.Tests.Validation;

public sealed class PointerEventValidationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);

    public static TheoryData<double> NonFiniteValues => new()
    {
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
    };

    [Fact]
    public void Validate_AcceptsValidMessage()
    {
        var result = ContractValidator.Validate(CreateValidMessage(), Now);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [MemberData(nameof(NonFiniteValues))]
    public void Validate_RejectsNonFiniteX(double value)
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { NormalizedX = value },
            Now);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == ValidationErrors.OutOfRange);
    }

    [Theory]
    [InlineData(-0.0001d)]
    [InlineData(1.0001d)]
    public void Validate_RejectsCoordinateOutsideInclusiveRange(double value)
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { NormalizedY = value },
            Now);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    public void Validate_AcceptsCoordinateBoundary(double value)
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { NormalizedX = value, NormalizedY = value },
            Now);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsMessageOlderThanTtl()
    {
        var message = CreateValidMessage() with
        {
            SentAtUnixMilliseconds = Now.ToUnixTimeMilliseconds() - 2_001,
        };

        var result = ContractValidator.Validate(message, Now);

        Assert.Contains(result.Errors, error => error.Code == ValidationErrors.Expired);
    }

    [Fact]
    public void Validate_AcceptsMessageAtTtlBoundary()
    {
        var message = CreateValidMessage() with
        {
            SentAtUnixMilliseconds = Now.ToUnixTimeMilliseconds() - 2_000,
        };

        var result = ContractValidator.Validate(message, Now);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsTimestampTooFarInFuture()
    {
        var message = CreateValidMessage() with
        {
            SentAtUnixMilliseconds = Now.ToUnixTimeMilliseconds() + 5_001,
        };

        var result = ContractValidator.Validate(message, Now);

        Assert.Contains(result.Errors, error => error.Code == ValidationErrors.FutureTimestamp);
    }

    [Fact]
    public void Validate_RejectsInvalidIdentitySequenceKindAndTtl()
    {
        var message = CreateValidMessage() with
        {
            EventId = Guid.Empty,
            SessionId = " ",
            SequenceNumber = -1,
            Kind = (PointerKind)99,
            TimeToLiveMilliseconds = 0,
        };

        var result = ContractValidator.Validate(message, Now);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 5);
    }

    private static PointerEventMessage CreateValidMessage() => new(
        Guid.Parse("4b646d0f-bfd8-4f77-949f-d18d67cc1879"),
        "session-id",
        42,
        0.25d,
        0.75d,
        PointerKind.Click,
        Now.ToUnixTimeMilliseconds(),
        2_000);
}
