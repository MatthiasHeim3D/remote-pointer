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

    public void Dispose()
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
