namespace RemoteAnnotate.Client.Configuration;

internal interface IUserProfileDefaultsProvider
{
    UserProfileDefaults GetCurrentProfile();
}

internal sealed record UserProfileDefaults(string? UserName, byte[]? Picture);
