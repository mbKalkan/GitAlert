using System.Runtime.InteropServices;

namespace GitAlert.Platform;

/// <summary>
/// An invisible top-level window that exists only to receive messages: the shell's tray callbacks
/// and its <c>TaskbarCreated</c> broadcast. It belongs to the thread that creates it, whose message
/// loop - WPF's or Avalonia's - delivers to <see cref="NativeMethods.WndProc"/> like any other window.
/// </summary>
/// <remarks>
/// A message-only window (one parented to <c>HWND_MESSAGE</c>) would be the textbook choice, but
/// such windows are left out of broadcasts, and Explorer announces its restart by broadcast. A
/// hidden top-level window gets both.
/// </remarks>
internal sealed class MessageWindow : IDisposable
{
    /// <summary>Kept in a field so the native side never calls a collected delegate.</summary>
    private readonly NativeMethods.WndProc _procedure;
    private readonly string _className;
    private readonly IntPtr _module;
    private bool _disposed;

    /// <summary>
    /// The handler answers a message with a result, or null to hand it to <c>DefWindowProc</c>.
    /// </summary>
    public MessageWindow(string name, Func<IntPtr, int, IntPtr, IntPtr, IntPtr?> handler)
    {
        _procedure = (hwnd, msg, wParam, lParam) =>
            handler(hwnd, (int)msg, wParam, lParam) ?? NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

        // One class per instance, so a second window in the same process can never collide with
        // a class a disposed one has not yet unregistered.
        _className = $"{name}.{Guid.NewGuid():N}";
        _module = NativeMethods.GetModuleHandle(null);

        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procedure),
            hInstance = _module,
            lpszClassName = _className,
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("Could not register the tray icon's window class.");
        }

        Handle = NativeMethods.CreateWindowEx(
            0,
            _className,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            _module,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            NativeMethods.UnregisterClass(_className, _module);
            throw new InvalidOperationException("Could not create the tray icon's window.");
        }
    }

    public IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        NativeMethods.UnregisterClass(_className, _module);
    }
}
