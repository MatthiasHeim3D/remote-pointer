using RemoteAnnotate.Contracts.Messages;

namespace RemoteAnnotate.Contracts.Tests.Messages;

public sealed class AnnotationColorsTests
{
    [Theory]
    [InlineData("#FF5C5C")]
    [InlineData("#012345")]
    [InlineData("#ABCDEF")]
    public void IsValid_AcceptsCanonicalForm(string color) =>
        Assert.True(AnnotationColors.IsValid(color));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#ff5c5c")]
    [InlineData("FF5C5C")]
    [InlineData("#FF5C5")]
    [InlineData("#FF5C5CC")]
    [InlineData("#GG0000")]
    [InlineData(" #FF5C5C")]
    public void IsValid_RejectsAnythingElse(string? color) =>
        Assert.False(AnnotationColors.IsValid(color));

    [Theory]
    [InlineData("#ff5c5c")]
    [InlineData("  #Ff5C5c  ")]
    public void Normalize_UpperCasesAndTrims(string color) =>
        Assert.Equal("#FF5C5C", AnnotationColors.Normalize(color));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("crimson")]
    [InlineData("#80FF5C5C")]
    public void Normalize_FallsBackToDefaultForUnusableValues(string? color) =>
        Assert.Equal(AnnotationColors.Default, AnnotationColors.Normalize(color));

    [Fact]
    public void Default_IsItselfCanonical() =>
        Assert.True(AnnotationColors.IsValid(AnnotationColors.Default));
}
