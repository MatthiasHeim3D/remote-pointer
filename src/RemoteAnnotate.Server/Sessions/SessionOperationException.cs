namespace RemoteAnnotate.Server.Sessions;

internal sealed class SessionOperationException(string code, string message)
    : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}
