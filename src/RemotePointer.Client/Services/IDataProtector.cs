namespace RemotePointer.Client.Services;

public interface IDataProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}
