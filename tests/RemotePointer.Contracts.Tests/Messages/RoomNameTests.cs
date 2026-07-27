using RemotePointer.Contracts.Messages;

namespace RemotePointer.Contracts.Tests.Messages;

public sealed class RoomNameTests
{
    [Theory]
    [InlineData("engineering", "engineering")]
    [InlineData("Engineering", "engineering")]
    [InlineData("  Engineering  ", "engineering")]
    [InlineData("ENGINEERING", "engineering")]
    public void Normalize_FoldsCaseAndSurroundingSpaceSoTypedNamesMeet(
        string typed,
        string expected)
    {
        Assert.Equal(expected, RoomName.Normalize(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("with\tcontrol")]
    public void Normalize_FallsBackToTheDefaultRoomForAnythingUnusable(string? typed)
    {
        Assert.Equal(RoomName.Default, RoomName.Normalize(typed));
    }

    [Fact]
    public void Normalize_FallsBackToTheDefaultRoomForANameTooLongToShow()
    {
        var longest = new string('r', RoomName.MaximumLength);

        Assert.Equal(longest, RoomName.Normalize(longest));
        Assert.Equal(RoomName.Default, RoomName.Normalize(longest + "r"));
    }

    [Fact]
    public void IsValid_RejectsWhatNormalizeWouldReplace()
    {
        Assert.True(RoomName.IsValid("engineering"));
        Assert.True(RoomName.IsValid("  engineering  "));
        Assert.False(RoomName.IsValid(null));
        Assert.False(RoomName.IsValid("   "));
        Assert.False(RoomName.IsValid(new string('r', RoomName.MaximumLength + 1)));
    }

    [Fact]
    public void Default_IsAUsableNameInItsOwnRight()
    {
        // The relay puts unnamed connections here, so it has to survive the same round trip
        // any typed name does.
        Assert.True(RoomName.IsValid(RoomName.Default));
        Assert.Equal(RoomName.Default, RoomName.Normalize(RoomName.Default));
    }

    [Fact]
    public void DefaultDisplayName_NamesTheSameRoomTheRelayPutsUnnamedConnectionsIn()
    {
        // A fresh client shows and stores this one. If it folded to anything else, that client
        // would sit in a room of its own while believing it was in the default one.
        Assert.True(RoomName.IsValid(RoomName.DefaultDisplayName));
        Assert.Equal(RoomName.Default, RoomName.Normalize(RoomName.DefaultDisplayName));
    }
}
