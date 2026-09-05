using System.Text.RegularExpressions;
using GitAlert.Core;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The glyph table hands the view path data and palette keys, never brushes or geometry, so it has
/// to answer for every kind and every key on its own.
/// </summary>
public class AlertGlyphsTests
{
    [Fact]
    public void A_severity_outranks_the_kind_when_choosing_the_accent() =>
        Assert.Equal("SeverityError", AlertGlyphs.AccentKeyFor(AlertKind.Workflow, AlertSeverity.Error));

    [Fact]
    public void Without_a_severity_the_kind_names_the_accent() =>
        Assert.Equal("KindPush", AlertGlyphs.AccentKeyFor(AlertKind.Push, AlertSeverity.Normal));

    [Fact]
    public void An_unknown_kind_draws_the_generic_dot() =>
        Assert.Equal(AlertGlyphs.PathFor(AlertKind.Other), AlertGlyphs.PathFor((AlertKind)999));

    [Fact]
    public void Every_kind_has_path_data_and_a_fallback_colour()
    {
        foreach (var kind in Enum.GetValues<AlertKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AlertGlyphs.PathFor(kind)), $"{kind} has no glyph");
            Assert.Matches("^#[0-9A-F]{6}$", AlertGlyphs.FallbackColourFor("Kind" + kind));
        }
    }

    [Fact]
    public void A_key_no_palette_knows_falls_back_to_the_generic_colour() =>
        Assert.Equal(AlertGlyphs.FallbackColourFor("KindOther"), AlertGlyphs.FallbackColourFor("Kind999"));
}
