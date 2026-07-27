namespace RemotePointer.Server.Security;

public sealed class ServerAccessOptions
{
    public const string SectionName = "Access";

    /// <summary>
    /// The password every client must present before this relay will talk to it at all. It is
    /// the relay's whole front door: without it a client cannot publish itself, list anyone, or
    /// reach a session, whatever room it names. Leaving it empty runs the relay open, which is
    /// meant for local development — anyone who can reach the address is then a client.
    /// </summary>
    public string? ServerPassword { get; set; }
}
