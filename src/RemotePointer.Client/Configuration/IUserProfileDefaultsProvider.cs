namespace RemotePointer.Client.Configuration;

internal interface IUserProfileDefaultsProvider
{
    UserProfileDefaults GetCurrentProfile();
}

internal sealed record UserProfileDefaults(string? UserName, byte[]? Picture);
