namespace GitAlert.Core;

/// <summary>A point on the desktop in physical pixels, the unit the shell reports tray clicks in.</summary>
public readonly record struct ScreenPoint(int X, int Y);
