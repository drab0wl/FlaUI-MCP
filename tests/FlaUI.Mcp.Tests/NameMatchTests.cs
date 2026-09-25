using System.Text.Json;
using PlaywrightWindows.Mcp.Core.Batch;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class NameMatchTests
{
    [Theory]
    [InlineData("Save &As...", "save as")]
    [InlineData("&OK", "ok")]
    [InlineData("Name:", "name")]
    [InlineData("Open…", "open")]
    [InlineData("Save\tCtrl+S", "save")]
    [InlineData("Tom && Jerry", "tom & jerry")]
    [InlineData("  Find   and\nReplace  ", "find and replace")]
    [InlineData(null, "")]
    public void Normalize(string? name, string expected)
    {
        Assert.Equal(expected, NameMatch.Normalize(name));
    }

    [Fact]
    public void LooselyEquals_IgnoresDecoration_ButNotWords()
    {
        Assert.True(NameMatch.LooselyEquals("Save As", "Save &As..."));
        Assert.True(NameMatch.LooselyEquals("ok", "&OK"));
        Assert.False(NameMatch.LooselyEquals("Save", "Save As..."));
        Assert.False(NameMatch.LooselyEquals("", ""));
    }

    [Fact]
    public void Suggest_RanksCloseNamesFirst_AndDropsUnrelated()
    {
        var names = new[] { "Save", "Save All", "Save As...", "Open", "Close Window", "Print Preview" };

        var suggestions = NameMatch.Suggest("Save Al", names, n => n);

        Assert.Equal("Save All", suggestions[0]);
        Assert.Contains("Save", suggestions);
        Assert.DoesNotContain("Print Preview", suggestions);
        Assert.DoesNotContain("Open", suggestions);
    }

    [Fact]
    public void Suggest_CatchesTypos()
    {
        var suggestions = NameMatch.Suggest("Setings", new[] { "Settings", "Help", "About" }, n => n);
        Assert.Equal(new[] { "Settings" }, suggestions);
    }

    [Fact]
    public void Pick_PrefersExactThenLoose()
    {
        var items = new[] { "Save As...", "Save As", "Open" };
        Assert.Equal("Save As", NameMatch.Pick(items, "Save As", s => s));
        Assert.Equal("Save As...", NameMatch.Pick(new[] { "Open", "Save As..." }, "save as", s => s));
        Assert.Null(NameMatch.Pick(items, "Print", s => s));
    }

    [Fact]
    public void MenuPath_ParsesArraysAndStrings()
    {
        JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

        Assert.Equal(new[] { "File", "Save As..." }, MenuPath.Parse(Json("""{"path":["File"," Save As... ",""]}""")));
        Assert.Equal(new[] { "Edit", "Find", "Replace" }, MenuPath.Parse(Json("""{"path":"Edit > Find >Replace"}""")));
        Assert.Equal(new[] { "View", "Other Windows" }, MenuPath.Parse(Json("""{"path":"View -> Other Windows"}""")));
        Assert.Null(MenuPath.Parse(Json("""{"action":"menu"}""")));
        Assert.Equal("File > Save As...", MenuPath.Describe(new[] { "File", "Save As..." }));
    }
}
