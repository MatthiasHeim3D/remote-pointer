using RemotePointer.Client.Views;

namespace RemotePointer.Client.Tests.Views;

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
}
