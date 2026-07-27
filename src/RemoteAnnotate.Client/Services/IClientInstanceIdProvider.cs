namespace RemoteAnnotate.Client.Services;

public interface IClientInstanceIdProvider
{
    string GetClientInstanceId();

    string GetApplicationInstanceId();
}
