using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GitAlert.Core;

namespace GitAlert.Configuration;

/// <summary>
/// Stores each account's GitHub token encrypted with DPAPI under the current user account, so a
/// blob on disk is useless to any other user or machine. One file per account, named by the
/// account id, which keeps a work token and a personal token completely separate.
/// </summary>
/// <remarks>
/// DPAPI is reached through <c>crypt32.dll</c> directly rather than through the
/// <c>System.Security.Cryptography.ProtectedData</c> package - it keeps GitAlert free of an
/// extra dependency for roughly forty lines of interop.
/// </remarks>
public sealed class SecureTokenStore
{
    private const string EntropyLabel = "GitAlert.Token.v1";

    private readonly string _directory;
    private readonly string _legacyFile;

    public SecureTokenStore(string? dataDirectory = null)
    {
        var root = dataDirectory ?? AppPaths.DataDirectory;

        _directory = Path.Combine(root, "tokens");
        _legacyFile = Path.Combine(root, "token.bin");
    }

    /// <summary>
    /// Whether an account id may be used as a file name.
    /// </summary>
    /// <remarks>
    /// Ids are generated as a bare GUID and never typed, but they arrive here by way of
    /// settings.json, which is a text file in a folder the user can open. An id of
    /// <c>..\..\something</c> would put a token wherever the process can write. There is no
    /// legitimate id outside this shape, so the answer is to refuse one rather than to repair it.
    /// </remarks>
    public static bool IsValidAccountId(string? accountId) =>
        accountId is { Length: > 0 and <= 64 }
        && accountId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public bool Has(string accountId) => PathFor(accountId) is { } path && File.Exists(path);

    public string? Read(string accountId) => PathFor(accountId) is { } path ? ReadFile(path) : null;

    public void Write(string accountId, string token)
    {
        var path = PathFor(accountId)
            ?? throw new ArgumentException($"'{accountId}' is not a usable account id.", nameof(accountId));

        Directory.CreateDirectory(_directory);

        var protectedBytes = Protect(Encoding.UTF8.GetBytes(token))
            ?? throw new InvalidOperationException("Windows refused to encrypt the access token.");

        File.WriteAllBytes(path, protectedBytes);
    }

    public void Delete(string accountId)
    {
        if (PathFor(accountId) is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Reads every stored token, keyed by account id, skipping any that fail to decrypt.</summary>
    public Dictionary<string, string> ReadAll(IEnumerable<string> accountIds)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in accountIds)
        {
            if (Read(id) is { } token)
            {
                tokens[id] = token;
            }
        }

        return tokens;
    }

    /// <summary>Removes token files that no longer belong to any configured account.</summary>
    public void Prune(IEnumerable<string> keep)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var live = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            if (!live.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // A locked file simply lingers; it is inert either way.
                }
            }
        }
    }

    // ---- Legacy single-token layout ----------------------------------------

    /// <summary>The token written by versions before multi-account support, if one is still there.</summary>
    public string? ReadLegacy() => ReadFile(_legacyFile);

    public bool HasLegacy => File.Exists(_legacyFile);

    public void DeleteLegacy()
    {
        if (File.Exists(_legacyFile))
        {
            File.Delete(_legacyFile);
        }
    }

    private string? PathFor(string accountId) =>
        IsValidAccountId(accountId) ? Path.Combine(_directory, accountId + ".bin") : null;

    private static string? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plain = Unprotect(File.ReadAllBytes(path));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // A blob written by another user account (or a corrupted file) cannot be decrypted.
            // Treat it as "no token" so the user is simply asked again.
            return null;
        }
    }

    private static byte[]? Protect(byte[] plain) =>
        Transform(plain, Encoding.UTF8.GetBytes(EntropyLabel), encrypt: true);

    private static byte[]? Unprotect(byte[] cipher) =>
        Transform(cipher, Encoding.UTF8.GetBytes(EntropyLabel), encrypt: false);

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
