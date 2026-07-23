using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Services;

public interface IProtectedSessionStore
{
    SessionCredential? Load(ClientRole role, string clientInstanceId);

    void Save(SessionCredential credential);

    void Clear(ClientRole role);
}
