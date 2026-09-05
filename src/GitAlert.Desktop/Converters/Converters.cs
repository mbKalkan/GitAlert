using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.ViewModels;

namespace GitAlert.Converters;

/// <summary>
/// True when the value's text equals the parameter. Feeds a style class from an enum property -
/// <c>Classes.added="{Binding Kind, Converter={StaticResource Equals}, ConverterParameter=Added}"</c> -
/// which is how a data-driven look is expressed here in place of WPF's data triggers.
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The first value while the second is true, nothing otherwise. Lets a list bind its items only
/// while the card above it is the open one, so folded cards hold no row containers.
/// </summary>
public sealed class WhenTrueConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[1] is true ? values[0] : null;
}

/// <summary>Formats the poll interval options as readable labels in the settings combo box.</summary>
public sealed class MinutesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            1 => "Every minute",
            60 => "Every hour",
            int minutes => $"Every {minutes} minutes",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns the theme enum into the label shown in the settings combo box.</summary>
public sealed class AppThemeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            AppTheme.System => "Follow the system",
            AppTheme.Dark => "Dark",
            AppTheme.Light => "Light",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns the dark palette enum into the label shown in the settings combo box.</summary>
public sealed class DarkPaletteConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DarkPalette.VsCode => "VS Code Dark Modern",
            DarkPalette.GitHub => "GitHub",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats the history-size options.</summary>
public sealed class HistorySizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count ? $"Keep {count} alerts" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>An <see cref="AlertKind"/> to its 16 x 16 glyph, parsed once per kind.</summary>
public sealed class KindToGlyphConverter : IValueConverter
{
    private static readonly Dictionary<AlertKind, Geometry> Cache = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is AlertKind known ? known : AlertKind.Other;

        if (!Cache.TryGetValue(kind, out var geometry))
        {
            geometry = StreamGeometry.Parse(AlertGlyphs.PathFor(kind));
            Cache[kind] = geometry;
        }

        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A palette key to the brush the loaded palette holds under it, so the glyphs recolour with the
/// theme. A process with no palette loaded, such as the tests, gets the kind's own colour.
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, IBrush> Fallbacks = [];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key)
        {
            return AvaloniaProperty.UnsetValue;
        }

        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            && resource is IBrush themed)
        {
            return themed;
        }

        if (!Fallbacks.TryGetValue(key, out var brush))
        {
            brush = new ImmutableSolidColorBrush(Color.Parse(AlertGlyphs.FallbackColourFor(key)));
            Fallbacks[key] = brush;
        }

        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
