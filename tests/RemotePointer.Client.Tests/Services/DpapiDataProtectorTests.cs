using System.Security.Cryptography;
using RemotePointer.Client.Services;

namespace RemotePointer.Client.Tests.Services;

public sealed class DpapiDataProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsForCurrentWindowsUser()
    {
        var protector = new DpapiDataProtector();
        var plaintext = RandomNumberGenerator.GetBytes(64);

        var protectedData = protector.Protect(plaintext);
        var recovered = protector.Unprotect(protectedData);

        Assert.NotEqual(plaintext, protectedData);
        Assert.Equal(plaintext, recovered);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(recovered);
    }
}
