using RemoteAnnotate.Contracts.Messages;
using RemoteAnnotate.Server.Sessions;

namespace RemoteAnnotate.Server.Tests.Sessions;

public sealed class AnnotationColorAllocatorTests
{
    [Fact]
    public void Allocate_LeavesDistinctPreferencesAlone()
    {
        var allocated = AnnotationColorAllocator.Allocate(
            ["#FF5C5C", "#6CCB7F", "#B388FF"]);

        Assert.Equal(["#FF5C5C", "#6CCB7F", "#B388FF"], allocated);
    }

    [Fact]
    public void Allocate_KeepsTheEarlierAnnotatorOnAContestedColor()
    {
        var allocated = AnnotationColorAllocator.Allocate(["#FF5C5C", "#FF5C5C"]);

        Assert.Equal("#FF5C5C", allocated[0]);
        Assert.NotEqual("#FF5C5C", allocated[1]);
        Assert.Contains(allocated[1], AnnotationColors.Palette);
    }

    [Fact]
    public void Allocate_HonoursACustomColorNobodyElseHolds()
    {
        var allocated = AnnotationColorAllocator.Allocate(["#FF5C5C", "#123456"]);

        Assert.Equal(["#FF5C5C", "#123456"], allocated);
    }

    [Fact]
    public void Allocate_MovesTheSecondHolderOfACustomColorOntoThePalette()
    {
        var allocated = AnnotationColorAllocator.Allocate(["#123456", "#123456"]);

        Assert.Equal("#123456", allocated[0]);
        Assert.Contains(allocated[1], AnnotationColors.Palette);
    }

    [Fact]
    public void Allocate_GivesEveryAnnotatorItsOwnColorUpToPaletteCapacity()
    {
        var everyoneWantsRed = Enumerable
            .Repeat<string?>(AnnotationColors.Default, AnnotationColors.Palette.Count)
            .ToArray();

        var allocated = AnnotationColorAllocator.Allocate(everyoneWantsRed);

        Assert.Equal(
            AnnotationColors.Palette.Count,
            allocated.Distinct(StringComparer.Ordinal).Count());
        Assert.All(allocated, color => Assert.Contains(color, AnnotationColors.Palette));
    }

    [Fact]
    public void Allocate_RepeatsOnlyOnceThePaletteIsExhausted()
    {
        var capacity = AnnotationColors.Palette.Count;
        var overCapacity = Enumerable
            .Repeat<string?>(AnnotationColors.Default, capacity + 3)
            .ToArray();

        var allocated = AnnotationColorAllocator.Allocate(overCapacity);

        // Every colour is still a preset, all of them are in use, and the three that had to
        // double up are spread across distinct colours rather than piling onto one.
        Assert.All(allocated, color => Assert.Contains(color, AnnotationColors.Palette));
        Assert.Equal(capacity, allocated.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, allocated.GroupBy(color => color, StringComparer.Ordinal).Max(g => g.Count()));
        Assert.Equal(3, allocated.GroupBy(color => color, StringComparer.Ordinal).Count(g => g.Count() == 2));
    }

    [Fact]
    public void Allocate_ReadsMissingOrMalformedPreferencesAsTheDefault()
    {
        var allocated = AnnotationColorAllocator.Allocate([null, "nonsense"]);

        Assert.Equal(AnnotationColors.Default, allocated[0]);
        Assert.NotEqual(AnnotationColors.Default, allocated[1]);
    }

    [Fact]
    public void Allocate_IsStableSoAReallocationOnlyMovesWhatChanged()
    {
        var before = AnnotationColorAllocator.Allocate(["#FF5C5C", "#6CCB7F", "#FF5C5C"]);

        // The middle annotator leaves; the other two keep exactly what they had.
        var after = AnnotationColorAllocator.Allocate(["#FF5C5C", "#FF5C5C"]);

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[2], after[1]);
    }

    [Fact]
    public void Allocate_ReturnsADisplacedAnnotatorToItsPreferenceWhenTheHolderLeaves()
    {
        var whileContested = AnnotationColorAllocator.Allocate(["#FF5C5C", "#FF5C5C"]);
        Assert.NotEqual("#FF5C5C", whileContested[1]);

        var afterTheHolderLeft = AnnotationColorAllocator.Allocate(["#FF5C5C"]);

        Assert.Equal("#FF5C5C", afterTheHolderLeft[0]);
    }

    [Fact]
    public void Allocate_AcceptsAnEmptySession() =>
        Assert.Empty(AnnotationColorAllocator.Allocate([]));
}
