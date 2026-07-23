using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class JsonFileClientAuditLogTests
{
    [Fact]
    public void Write_UsesStructuredRecordWithoutExceptionMessageOrCoordinates()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"RemotePointer.Tests.{Guid.NewGuid():N}");
        try
        {
            var auditLog = new JsonFileClientAuditLog(directory);
            auditLog.Write(
                ClientAuditEvent.UnhandledException,
                ClientAuditLevel.Error,
                exception: new InvalidOperationException(
                    "secret-token normalizedX=0.25 normalizedY=0.75"));

            var content = File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));

            Assert.Contains("unhandledException", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", content, StringComparison.Ordinal);
            Assert.DoesNotContain("normalizedX", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0.25", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
