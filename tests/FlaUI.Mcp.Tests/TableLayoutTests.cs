using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Tables;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class TableLayoutTests
{
    private static TableNode N(ControlType type, string? name = null, string? value = null) => new(type, name, value);

    [Fact]
    public void Classify_HeadersRowsAndContainers()
    {
        Assert.Equal(TableLayout.Kind.Header, TableLayout.Classify(N(ControlType.Header)));
        Assert.Equal(TableLayout.Kind.Row, TableLayout.Classify(N(ControlType.DataItem, "Row 0")));
        Assert.Equal(TableLayout.Kind.Row, TableLayout.Classify(N(ControlType.ListItem, "Photo.jpg")));
        Assert.Equal(TableLayout.Kind.Container, TableLayout.Classify(N(ControlType.Pane)));
        Assert.Equal(TableLayout.Kind.Other, TableLayout.Classify(N(ControlType.ScrollBar)));
        Assert.Equal(TableLayout.Kind.Other, TableLayout.Classify(N(ControlType.Text, "caption")));
        // WinForms DataGridView's "Top Row" is a row of header cells.
        Assert.Equal(TableLayout.Kind.HeaderRow, TableLayout.Classify(N(ControlType.Custom, "Top Row"),
            new[] { ControlType.HeaderItem, ControlType.HeaderItem }));
    }

    [Fact]
    public void Cells_PreferValues_SkipRowHeaders_FallBackToRowName()
    {
        var cells = TableLayout.Cells(N(ControlType.Custom, "Row 0"), new[]
        {
            N(ControlType.HeaderItem, "Row 0"),
            N(ControlType.DataItem, "ID Row 0", "1"),
            N(ControlType.DataItem, "Name Row 0", "Alpha\tBeta\nGamma"),
            N(ControlType.Text, "Plain"),
        });
        Assert.Equal(new[] { "1", "Alpha Beta\\nGamma", "Plain" }, cells);

        Assert.Equal(new[] { "Photo.jpg" }, TableLayout.Cells(N(ControlType.ListItem, "Photo.jpg"), Array.Empty<TableNode>()));
    }

    [Fact]
    public void HeaderNames_AndAlign()
    {
        var headers = TableLayout.HeaderNames(new[]
        {
            N(ControlType.HeaderItem, ""),
            N(ControlType.HeaderItem, "ID"),
            N(ControlType.HeaderItem, "Name"),
            N(ControlType.Thumb),
        });
        Assert.Equal(new[] { "", "ID", "Name" }, headers);
        Assert.Equal(new[] { "ID", "Name" }, TableLayout.Align(headers, cellCount: 2));
        Assert.Equal(new[] { "", "ID", "Name" }, TableLayout.Align(headers, cellCount: 3));
    }

    [Fact]
    public void Render_TsvWithRefsAndPaging()
    {
        var text = TableLayout.Render("grid \"Test Data\" [ref=w1e30]", new[] { "ID", "Name" },
            new List<(string, IReadOnlyList<string>)> { ("w1e31", new[] { "1", "Alpha" }), ("w1e32", new[] { "2", "Beta" }) },
            start: 0, available: 5, total: 40, more: true);

        Assert.Equal(new[]
        {
            "grid \"Test Data\" [ref=w1e30]: rows 1-2 of 5 (40 in total; the rest aren't loaded: scroll, or select/find by name)",
            "ref\tID\tName",
            "w1e31\t1\tAlpha",
            "w1e32\t2\tBeta",
            "... more rows: call again with start=2.",
        }, text.Split('\n'));
    }

    [Fact]
    public void Render_Empty()
    {
        Assert.Equal("list \"Files\" [ref=w1e3]: no rows",
            TableLayout.Render("list \"Files\" [ref=w1e3]", Array.Empty<string>(), new List<(string, IReadOnlyList<string>)>(), 0, 0, null, false));
    }
}
