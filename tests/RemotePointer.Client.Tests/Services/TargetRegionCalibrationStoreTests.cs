using RemotePointer.Client.Services;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Tests.Services;

public sealed class TargetRegionCalibrationStoreTests : IDisposable
{
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"RemotePointer.CalibrationTests.{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndLoad_UsesSeparateCalibrationPerHost()
    {
        var store = new TargetRegionCalibrationStore(directoryPath);
        var first = new RectangleD(10d, 20d, 800d, 450d);
        var second = new RectangleD(30d, 40d, 640d, 480d);

        store.Save("host-one", first);
        store.Save("host-two", second);

        Assert.Equal(first, store.Load("host-one"));
        Assert.Equal(second, store.Load("host-two"));
        Assert.Null(store.Load("unknown-host"));
    }

    [Fact]
    public void Save_IgnoresAnUnusableCalibrationDirectory()
    {
        var blockedPath = Path.Combine(directoryPath, "blocked");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(blockedPath, "not a directory");
        var store = new TargetRegionCalibrationStore(blockedPath);

        var exception = Record.Exception(
            () => store.Save("host-one", new RectangleD(10d, 20d, 800d, 450d)));

        Assert.Null(exception);
        Assert.Null(store.Load("host-one"));
    }

    [Fact]
    public void Save_StillRejectsAnInvalidRectangle()
    {
        var store = new TargetRegionCalibrationStore(directoryPath);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Save("host-one", new RectangleD(0d, 0d, 10d, 10d)));
    }

    public void Dispose()
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
