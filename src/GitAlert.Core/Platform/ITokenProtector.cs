namespace GitAlert.Platform;

/// <summary>
/// Encrypts a token for the disk and decrypts it again, bound to the signed-in user so a copied
/// file is useless anywhere else. Windows answers with DPAPI; other platforms bring their own.
/// </summary>
public interface ITokenProtector
{
    /// <summary>The protected bytes, or null when the platform refused.</summary>
    byte[]? Protect(byte[] plain);

    /// <summary>The original bytes, or null when the blob belongs to another user or is damaged.</summary>
    byte[]? Unprotect(byte[] cipher);
}
