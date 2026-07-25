using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Tests.Services;

public sealed class TargetRegionCalibrationStoreTests : IDisposable
{
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"RemotePointer.CalibrationTests.{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndLoad_UsesSeparateCalibrationPerReceiver()
    {
        var store = new TargetRegionCalibrationStore(directoryPath);
        var first = new RectangleD(10d, 20d, 800d, 450d);
        var second = new RectangleD(30d, 40d, 640d, 480d);

        store.Save("receiver-one", first);
        store.Save("receiver-two", second);

        Assert.Equal(first, store.Load("receiver-one"));
        Assert.Equal(second, store.Load("receiver-two"));
        Assert.Null(store.Load("unknown-receiver"));
    }

    [Fact]
    public void Save_IgnoresAnUnusableCalibrationDirectory()
    {
        var blockedPath = Path.Combine(directoryPath, "blocked");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(blockedPath, "not a directory");
        var store = new TargetRegionCalibrationStore(blockedPath);

        var exception = Record.Exception(
            () => store.Save("receiver-one", new RectangleD(10d, 20d, 800d, 450d)));

        Assert.Null(exception);
        Assert.Null(store.Load("receiver-one"));
    }

    [Fact]
    public void Save_StillRejectsAnInvalidRectangle()
    {
        var store = new TargetRegionCalibrationStore(directoryPath);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Save("receiver-one", new RectangleD(0d, 0d, 10d, 10d)));
    }

    public void Dispose()
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
