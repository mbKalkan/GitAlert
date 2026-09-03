using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GitAlert.Platform;

/// <summary>
/// Rasterises <see cref="IconArtwork"/> into the two shapes Windows wants: a live <c>HICON</c>
/// for the notification area, and a multi-resolution <c>.ico</c> file for the executable.
/// </summary>
/// <remarks>Must be called from an STA thread - WPF rendering requires one.</remarks>
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

    /// <summary>
    /// Creates a tray icon handle. The caller owns the handle and must free it with
    /// <see cref="DestroyIcon"/>.
    /// </summary>
    public static IntPtr CreateTrayIcon(int size, Color foreground, Color? badge)
    {
        var pixels = Render(size, context => IconArtwork.DrawTrayIcon(context, size, foreground, badge));
        return CreateHIcon(pixels, size, size);
    }

    public static IntPtr CreateAppIcon(int size)
    {
        var pixels = Render(size, context => IconArtwork.DrawAppIcon(context, size));
        return CreateHIcon(pixels, size, size);
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
    public static void WriteApplicationIcon(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var frames = AppIconSizes
            .Select(size => (Size: size, Data: BuildDibFrame(size)))
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

    /// <summary>Renders the artwork to premultiplied BGRA pixels, top-down.</summary>
    private static byte[] Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            draw(context);
        }

        // 96 DPI keeps one design unit equal to one device pixel, so `size` is exact.
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        target.CopyPixels(pixels, stride, 0);

        return pixels;
    }

    /// <summary>Builds one BITMAPINFOHEADER + XOR image + AND mask block for an ICO frame.</summary>
    private static byte[] BuildDibFrame(int size)
    {
        var pixels = Render(size, context => IconArtwork.DrawAppIcon(context, size));

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

    private static IntPtr CreateHIcon(byte[] bgraPixels, int width, int height)
    {
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,          // negative: top-down, matching WPF's pixel order
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
