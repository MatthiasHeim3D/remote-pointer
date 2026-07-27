using RemoteAnnotate.Client.Views;

namespace RemoteAnnotate.Client.Tests.Views;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(0, 244d)]
    [InlineData(1, 244d)]
    [InlineData(2, 308d)]
    [InlineData(4, 436d)]
    [InlineData(10, 436d)]
    public void CalculateClientListHeight_CapsVisibleRowsAtFour(
        int clientCount,
        double expectedHeight)
    {
        var height = MainWindow.CalculateClientListHeight(
            baseHeight: 244d,
            rowHeight: 64d,
            clientCount);

        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [InlineData(0, 244d)]
    [InlineData(1, 212d)]
    [InlineData(2, 276d)]
    [InlineData(4, 404d)]
    [InlineData(10, 404d)]
    public void CalculateAvailableClientsHeight_KeepsTheEmptyStateRoomierThanOneRow(
        int clientCount,
        double expectedHeight)
    {
        var height = MainWindow.CalculateAvailableClientsHeight(clientCount);

        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [InlineData(1, 200d)]
    [InlineData(2, 296d)]
    [InlineData(4, 400d)]
    [InlineData(10, 400d)]
    public void CalculateConnectedClientsHeight_AddsTheActionRowOnlyForMultipleAnnotators(
        int clientCount,
        double expectedHeight)
    {
        var height = MainWindow.CalculateConnectedClientsHeight(clientCount);

        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ConnectedHostAndConnectedAnnotator_StandAtTheSameHeight()
    {
        // Both panels draw one session row under a heading, so a session must look the same
        // height whether this client is the host or the annotator.
        Assert.Equal(
            MainWindow.AnnotatorSessionHeight,
            MainWindow.CalculateConnectedClientsHeight(1));
    }

    [Fact]
    public void CalculateConnectedClientsHeight_LeavesNoGapWhenTheSecondAnnotatorArrives()
    {
        var single = MainWindow.CalculateConnectedClientsHeight(1);
        var pair = MainWindow.CalculateConnectedClientsHeight(2);

        // One extra row plus the bulk-action row that only a second annotator brings into view.
        Assert.Equal(52d + 44d, pair - single);
    }
}
