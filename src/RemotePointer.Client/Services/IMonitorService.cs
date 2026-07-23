namespace RemotePointer.Client.Services;

public interface IMonitorService
{
    IReadOnlyList<MonitorDescriptor> GetMonitors();

    MonitorDescriptor? FindByDisplayId(string displayId);
}
