using System.IO;
using RemotePointer.Client.Configuration;

namespace RemotePointer.Client.Services;

public sealed class ClientInstanceIdProvider : IClientInstanceIdProvider
{
    private readonly string applicationInstanceId = Guid.NewGuid().ToString("N");
    private readonly object syncRoot = new();
    private string? cachedIdentifier;

    public string GetClientInstanceId()
    {
        lock (syncRoot)
        {
            if (cachedIdentifier is not null)
            {
                return cachedIdentifier;
            }

            var directory = ClientDataDirectory.Resolve();
            var path = Path.Combine(directory, "client-instance-id");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParseExact(existing, "N", out _))
                {
                    cachedIdentifier = existing;
                    return existing;
                }
            }

            Directory.CreateDirectory(directory);
            cachedIdentifier = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, cachedIdentifier);
            return cachedIdentifier;
        }
    }

    public string GetApplicationInstanceId() => applicationInstanceId;
}
