namespace RemotePointer.Client.Services;

public interface IStartupRegistrationService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
