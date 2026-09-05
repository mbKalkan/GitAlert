using System.Runtime.InteropServices;
using System.Text;

namespace GitAlert.Platform;

/// <summary>
/// DPAPI under the current user account: a blob on disk is useless to any other user or machine.
/// </summary>
/// <remarks>
/// <c>crypt32.dll</c> is called directly rather than through the
/// <c>System.Security.Cryptography.ProtectedData</c> package - it keeps GitAlert free of an extra
/// dependency for roughly forty lines of interop. The entropy label is part of every blob written
/// so far, so it cannot change without every stored token becoming unreadable.
/// </remarks>
public sealed class DpapiTokenProtector : ITokenProtector
{
    private const string EntropyLabel = "GitAlert.Token.v1";

    private const int CryptProtectUiForbidden = 0x1;

    public byte[]? Protect(byte[] plain) => Transform(plain, encrypt: true);

    public byte[]? Unprotect(byte[] cipher) => Transform(cipher, encrypt: false);

    private static byte[]? Transform(byte[] input, bool encrypt)
    {
        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);

        try
        {
            inputBlob = DataBlob.Allocate(input);
            entropyBlob = DataBlob.Allocate(Encoding.UTF8.GetBytes(EntropyLabel));

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
