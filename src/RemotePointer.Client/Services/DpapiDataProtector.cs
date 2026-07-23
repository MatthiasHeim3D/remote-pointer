using System.Security.Cryptography;
using System.Text;

namespace RemotePointer.Client.Services;

public sealed class DpapiDataProtector : IDataProtector
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("RemotePointer.SessionCredential.v1");

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, OptionalEntropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return ProtectedData.Unprotect(
            protectedData,
            OptionalEntropy,
            DataProtectionScope.CurrentUser);
    }
}
