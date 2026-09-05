namespace GitAlert.Core;

/// <summary>
/// A colour with no UI framework attached, for the few places the core has to name one: the tray
/// icon's foreground and badge. Each view turns it into its own brush type.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The <c>#RRGGBB</c> form, which every framework's colour parser understands.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
