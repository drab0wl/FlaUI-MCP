using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Snapshots;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class SnapshotFormatTests
{
    private static SnapshotNode Node(
        ControlType type = ControlType.Button,
        bool enabled = true,
        bool offscreen = false,
        bool readOnly = false,
        ToggleState? toggle = null,
        bool selected = false,
        ExpandCollapseState? expand = null) =>
        new("OK", null, type, enabled, offscreen, readOnly, toggle, selected, expand);

    [Theory]
    [InlineData(ControlType.Button, "button")]
    [InlineData(ControlType.Edit, "textbox")]
    [InlineData(ControlType.Pane, "group")]
    [InlineData(ControlType.DataItem, "row")]
    [InlineData(ControlType.AppBar, "element")]
    public void Role_MapsControlTypes(ControlType type, string role)
    {
        Assert.Equal(role, SnapshotFormat.Role(type));
    }

    [Fact]
    public void ControlTypesForRole_InvertsRoles_AndAcceptsUiaNames()
    {
        Assert.Equal(new[] { ControlType.Pane, ControlType.Group }, SnapshotFormat.ControlTypesForRole("group"));
        Assert.Equal(new[] { ControlType.Edit }, SnapshotFormat.ControlTypesForRole("TextBox"));
        Assert.Equal(new[] { ControlType.SplitButton }, SnapshotFormat.ControlTypesForRole("SplitButton"));
        Assert.Empty(SnapshotFormat.ControlTypesForRole("banana"));
        foreach (var role in SnapshotFormat.KnownRoles)
        {
            Assert.NotEmpty(SnapshotFormat.ControlTypesForRole(role));
        }
    }

    [Theory]
    [InlineData("Save", "btnSave", "Save")]
    [InlineData("  ", "btnSave", "[btnSave]")]
    [InlineData(null, "btnSave", "[btnSave]")]
    [InlineData(null, null, null)]
    public void DisplayName_FallsBackToShortAutomationId(string? name, string? automationId, string? expected)
    {
        Assert.Equal(expected, SnapshotFormat.DisplayName(name, automationId));
    }

    [Fact]
    public void DisplayName_IgnoresLongAutomationIds()
    {
        Assert.Null(SnapshotFormat.DisplayName(null, new string('x', 50)));
    }

    [Fact]
    public void States_InSnapshotOrder()
    {
        var states = SnapshotFormat.States(Node(enabled: false, offscreen: true, readOnly: true,
            toggle: ToggleState.On, selected: true, expand: ExpandCollapseState.Collapsed));
        Assert.Equal(new[] { "disabled", "offscreen", "readonly", "checked", "selected", "collapsed" }, states);

        Assert.Empty(SnapshotFormat.States(Node(toggle: ToggleState.Off, expand: ExpandCollapseState.LeafNode)));
        Assert.Equal(new[] { "indeterminate", "expanded" },
            SnapshotFormat.States(Node(toggle: ToggleState.Indeterminate, expand: ExpandCollapseState.Expanded)));
    }

    [Theory]
    [InlineData("OK", "element", false)]
    [InlineData(null, "element", true)]
    [InlineData(null, "scrollbar", true)]
    [InlineData(null, "titlebar", true)]
    [InlineData(null, "button", false)]
    [InlineData(null, "group", false)]
    [InlineData(null, "text", false)]
    public void ShouldSkip_UnnamedDecorations(string? name, string role, bool skip)
    {
        Assert.Equal(skip, SnapshotFormat.ShouldSkip(name, role));
    }

    [Fact]
    public void Line_MatchesOriginalFormat()
    {
        Assert.Equal("button \"Say \\\"hi\\\"\\n\" [ref=w1e3] [disabled] [checked]",
            SnapshotFormat.Line("w1e3", "Say \"hi\"\r\n", "button", new[] { "disabled", "checked" }));
        Assert.Equal("group [ref=w1e1]", SnapshotFormat.Line("w1e1", null, "group", Array.Empty<string>()));
        Assert.Equal("    ", SnapshotFormat.Indent(2));
    }
}
