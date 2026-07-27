using RemotePointer.Contracts.Coordinates;
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

    [Theory]
    [InlineData(PointerKind.PathStart)]
    [InlineData(PointerKind.PathUpdate)]
    [InlineData(PointerKind.PathEnd)]
    [InlineData(PointerKind.LineStart)]
    [InlineData(PointerKind.LineUpdate)]
    [InlineData(PointerKind.LineEnd)]
    [InlineData(PointerKind.RectangleStart)]
    [InlineData(PointerKind.RectangleUpdate)]
    [InlineData(PointerKind.RectangleEnd)]
    [InlineData(PointerKind.CircleStart)]
    [InlineData(PointerKind.CircleUpdate)]
    [InlineData(PointerKind.CircleEnd)]
    public void Validate_RequiresGestureIdForGestureEvents(PointerKind kind)
    {
        var result = ContractValidator.Validate(CreateValidMessage() with { Kind = kind }, Now);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Message.Contains("GestureId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsGestureEventWithGestureId()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with
            {
                Kind = PointerKind.PathUpdate,
                GestureId = Guid.NewGuid(),
            },
            Now);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsBoundedTextEvent()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with
            {
                Kind = PointerKind.Text,
                Text = "Please look here",
            },
            Now);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsMissingText(string? text)
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { Kind = PointerKind.Text, Text = text },
            Now);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsOversizedText()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with
            {
                Kind = PointerKind.Text,
                Text = new string('x', ContractValidator.MaximumPointerTextLength + 1),
            },
            Now);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsBoundedPathPointBatch()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with
            {
                Kind = PointerKind.PathUpdate,
                GestureId = Guid.NewGuid(),
                PathPoints =
                [
                    new(0.1d, 0.2d),
                    new(0.15d, 0.3d),
                    new(0.2d, 0.4d),
                ],
            },
            Now);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsEmptyPathPointBatchForKeepAlive()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with
            {
                Kind = PointerKind.PathUpdate,
                GestureId = Guid.NewGuid(),
                PathPoints = [],
            },
            Now);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsPathPointsOnNonPathEvent()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { PathPoints = [new(0.1d, 0.2d)] },
            Now);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsOversizedOrOutOfRangePathPointBatch()
    {
        var oversized = Enumerable.Repeat(
                new NormalizedPoint(0.5d, 0.5d),
                ContractValidator.MaximumPathPointsPerEvent + 1)
            .ToArray();
        oversized[^1] = new NormalizedPoint(1.1d, 0.5d);
        var result = ContractValidator.Validate(
            CreateValidMessage() with
            {
                Kind = PointerKind.PathEnd,
                GestureId = Guid.NewGuid(),
                PathPoints = oversized,
            },
            Now);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2);
    }

    [Theory]
    [InlineData("#FF5C5C")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void Validate_AcceptsCanonicalAnnotationColor(string color)
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { Color = color },
            Now);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AcceptsMissingAnnotationColor()
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { Color = null },
            Now);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FF5C5C")]
    [InlineData("#ff5c5c")]
    [InlineData("#FF5C5")]
    [InlineData("#FF5C5CC")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("#80FF5C5C")]
    public void Validate_RejectsMalformedAnnotationColor(string color)
    {
        var result = ContractValidator.Validate(
            CreateValidMessage() with { Color = color },
            Now);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == ValidationErrors.InvalidValue);
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
