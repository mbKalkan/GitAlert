using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GitAlert.Core;
using GitAlert.ViewModels;

namespace GitAlert.Converters;

/// <summary>An <see cref="AlertKind"/> to its 16 x 16 glyph, parsed once per kind and frozen.</summary>
public sealed class KindToGlyphConverter : IValueConverter
{
    private static readonly Dictionary<AlertKind, Geometry> Cache = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is AlertKind known ? known : AlertKind.Other;

        if (!Cache.TryGetValue(kind, out var geometry))
        {
            geometry = Geometry.Parse(AlertGlyphs.PathFor(kind));
            geometry.Freeze();
            Cache[kind] = geometry;
        }

        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A palette key to the brush the loaded theme holds under it, so the glyphs recolour with the
/// theme. A process with no palette loaded, such as the tests, gets the kind's own colour.
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Fallbacks = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key)
        {
            return DependencyProperty.UnsetValue;
        }

        if (Application.Current?.TryFindResource(key) is SolidColorBrush themed)
        {
            return themed;
        }

        if (!Fallbacks.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(AlertGlyphs.FallbackColourFor(key)));
            brush.Freeze();
            Fallbacks[key] = brush;
        }

        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
