namespace RemoteAnnotate.Client.Services;

public interface IServerPasswordStore
{
    /// <summary>Returns the stored group key, or null when no server password is set.</summary>
    string? Load();

    void Save(string groupKey);

    void Clear();
}
