using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemotePointer.Client.Configuration;
using RemotePointer.Contracts.Coordinates;

namespace RemotePointer.Client.Services;

public sealed class TargetRegionCalibrationStore(string? directoryPath = null)
{
    private readonly string directoryPath =
        directoryPath ?? ClientDataDirectory.Resolve("Calibrations");

    public RectangleD? Load(string receiverIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverIdentity);
        var path = GetPath(receiverIdentity);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var rectangle = JsonSerializer.Deserialize<RectangleD>(File.ReadAllText(path));
            return IsValid(rectangle) ? rectangle : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string receiverIdentity, RectangleD rectangle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverIdentity);
        if (!IsValid(rectangle))
        {
            throw new ArgumentOutOfRangeException(nameof(rectangle));
        }

        try
        {
            Directory.CreateDirectory(directoryPath);
            File.WriteAllText(GetPath(receiverIdentity), JsonSerializer.Serialize(rectangle));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Remembering the rectangle is a convenience. Load already ignores an unusable
            // file, and this runs from the Lock button, so a failure here would otherwise
            // reach the dispatcher handler and close the application mid-calibration.
        }
    }

    private string GetPath(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directoryPath, $"{hash}.json");
    }

    private static bool IsValid(RectangleD rectangle) =>
        double.IsFinite(rectangle.Left)
        && double.IsFinite(rectangle.Top)
        && double.IsFinite(rectangle.Width)
        && double.IsFinite(rectangle.Height)
        && rectangle.Width >= TargetRegionGeometry.MinimumWidth
        && rectangle.Height >= TargetRegionGeometry.MinimumHeight;
}
