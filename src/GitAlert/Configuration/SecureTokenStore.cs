using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GitAlert.Configuration;

/// <summary>
/// Stores the GitHub personal access token encrypted with DPAPI under the current user
/// account, so the blob on disk is useless to any other user or machine.
/// </summary>
/// <remarks>
/// DPAPI is reached through <c>crypt32.dll</c> directly rather than through the
/// <c>System.Security.Cryptography.ProtectedData</c> package - it keeps GitAlert free of an
/// extra dependency for roughly forty lines of interop.
/// </remarks>
public sealed class SecureTokenStore
{
    private const string EntropyLabel = "GitAlert.Token.v1";

    private readonly string _path;

    public SecureTokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(Core.AppPaths.DataDirectory, "token.bin");
    }

    public bool HasToken => File.Exists(_path);

    public string? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = Unprotect(protectedBytes);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // A blob written by another user account (or a corrupted file) cannot be
            // decrypted. Treat it as "no token" so the user is simply asked again.
            return null;
        }
    }

    public void Write(string token)
    {
        Core.AppPaths.EnsureCreated();

        var protectedBytes = Protect(Encoding.UTF8.GetBytes(token))
            ?? throw new InvalidOperationException("Windows refused to encrypt the access token.");

        File.WriteAllBytes(_path, protectedBytes);
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static byte[]? Protect(byte[] plain)
    {
        var entropy = Encoding.UTF8.GetBytes(EntropyLabel);
        return Transform(plain, entropy, encrypt: true);
    }

    private static byte[]? Unprotect(byte[] cipher)
    {
        var entropy = Encoding.UTF8.GetBytes(EntropyLabel);
        return Transform(cipher, entropy, encrypt: false);
    }

    private static byte[]? Transform(byte[] input, byte[] entropy, bool encrypt)
    {
        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);

        try
        {
            inputBlob = DataBlob.Allocate(input);
            entropyBlob = DataBlob.Allocate(entropy);

            var ok = encrypt
                ? CryptProtectData(ref inputBlob, EntropyLabel, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);

            if (!ok)
            {
                return null;
            }

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, outputBlob.cbData);
            return result;
        }
        finally
        {
            inputBlob.Free();
            entropyBlob.Free();
            outputBlob.FreeLocal();
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;

        public static DataBlob Allocate(byte[] data)
        {
            var blob = new DataBlob
            {
                cbData = data.Length,
                pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)),
            };

            if (data.Length > 0)
            {
                Marshal.Copy(data, 0, blob.pbData, data.Length);
            }

            return blob;
        }

        public void Free()
        {
            if (pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pbData);
                pbData = IntPtr.Zero;
            }
        }

        /// <summary>Buffers returned by DPAPI are owned by the caller and freed with LocalFree.</summary>
        public void FreeLocal()
        {
            if (pbData != IntPtr.Zero)
            {
                LocalFree(pbData);
                pbData = IntPtr.Zero;
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
