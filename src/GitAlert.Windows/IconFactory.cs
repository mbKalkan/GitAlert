using System.Runtime.InteropServices;

namespace GitAlert.Platform;

/// <summary>
/// Turns rendered pixels into the two shapes Windows wants: a live <c>HICON</c> for the
/// notification area, and a multi-resolution <c>.ico</c> file for the executable. Drawing the
/// artwork is the front end's job; this only knows about DIBs.
/// </summary>
public static class IconFactory
{
    /// <summary>Sizes written into <c>app.ico</c>, covering every shell surface up to 4K.</summary>
    private static readonly int[] AppIconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>The notification-area icon size the shell currently expects.</summary>
    public static int TraySize
    {
        get
        {
            var metric = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
            return metric is >= 16 and <= 64 ? metric : 16;
        }
    }

    public static void DestroyIcon(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Writes a multi-resolution <c>.ico</c>. Frames are stored as 32-bit BGRA DIBs, which every
    /// Windows shell surface and installer understands without relying on PNG-in-ICO support.
    /// </summary>
    /// <param name="render">Draws the artwork at a size, as premultiplied BGRA pixels, top-down.</param>
    public static void WriteApplicationIcon(string path, Func<int, byte[]> render)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var frames = AppIconSizes
            .Select(size => (Size: size, Data: BuildDibFrame(size, render(size))))
            .ToList();

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((short)0);              // reserved
        writer.Write((short)1);              // type: icon
        writer.Write((short)frames.Count);

        var offset = 6 + (16 * frames.Count);

        foreach (var (size, data) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));  // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);           // palette size
            writer.Write((byte)0);           // reserved
            writer.Write((short)1);          // colour planes
            writer.Write((short)32);         // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);

            offset += data.Length;
        }

        foreach (var (_, data) in frames)
        {
            writer.Write(data);
        }
    }

    /// <summary>Builds one BITMAPINFOHEADER + XOR image + AND mask block for an ICO frame.</summary>
    private static byte[] BuildDibFrame(int size, byte[] pixels)
    {
        var xorStride = size * 4;
        var maskStride = ((size + 31) / 32) * 4;
        var maskLength = maskStride * size;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        // BITMAPINFOHEADER - height is doubled because the AND mask follows the colour data.
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);                     // BI_RGB
        writer.Write(xorStride * size + maskLength);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // DIBs are stored bottom-up.
        for (var row = size - 1; row >= 0; row--)
        {
            writer.Write(pixels, row * xorStride, xorStride);
        }

        // A fully zeroed AND mask means "use the alpha channel", which 32-bit icons do.
        writer.Write(new byte[maskLength]);

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Wraps premultiplied BGRA pixels, top-down, in an icon handle. The caller owns the handle
    /// and frees it with <see cref="DestroyIcon"/>.
    /// </summary>
    public static IntPtr CreateHIcon(byte[] bgraPixels, int width, int height)
    {
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,          // negative: top-down, matching the renderers' pixel order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        var colourBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref info,
            NativeMethods.DIB_RGB_COLORS,
            out var bits,
            IntPtr.Zero,
            0);

        if (colourBitmap == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var maskStride = ((width + 31) / 32) * 4;
        var maskBitmap = NativeMethods.CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);

        try
        {
            Marshal.Copy(bgraPixels, 0, bits, bgraPixels.Length);

            var iconInfo = new NativeMethods.ICONINFO
            {
                fIcon = true,
                hbmColor = colourBitmap,
                hbmMask = maskBitmap,
            };

            return NativeMethods.CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            NativeMethods.DeleteObject(colourBitmap);
            NativeMethods.DeleteObject(maskBitmap);
        }
    }
}
