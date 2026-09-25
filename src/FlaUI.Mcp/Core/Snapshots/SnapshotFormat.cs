using System.Text;
using FlaUI.Core.Definitions;

namespace PlaywrightWindows.Mcp.Core.Snapshots;

/// <summary>
/// The properties a snapshot line needs, read either live (one UIA call per property) or from
/// a UIA cache (one call per batch of elements).
/// </summary>
public sealed record SnapshotNode(
    string? Name,
    string? AutomationId,
    ControlType ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsReadOnly,
    ToggleState? Toggle,
    bool IsSelected,
    ExpandCollapseState? ExpandCollapse)
{
    /// <summary>Runtime id as text, used to keep refs stable across snapshots. May be null.</summary>
    public string? RuntimeId { get; init; }
}

/// <summary>Bounds for a snapshot. The defaults match the original unbounded behavior.</summary>
public sealed record SnapshotLimits
{
    public static SnapshotLimits Unbounded { get; } = new();

    public int MaxDepth { get; init; } = 10;
    public int MaxNodes { get; init; } = int.MaxValue;
    public int MaxChars { get; init; } = int.MaxValue;

    /// <summary>Stop walking at this time (checked between elements). A single UIA call can't be interrupted.</summary>
    public DateTime? DeadlineUtc { get; init; }
}

/// <summary>
/// Pure formatting rules for snapshot lines, shared by every way of reading the tree.
/// </summary>
public static class SnapshotFormat
{
    private static readonly (ControlType Type, string Role)[] RoleTable =
    {
        (ControlType.Button, "button"),
        (ControlType.Edit, "textbox"),
        (ControlType.Text, "text"),
        (ControlType.CheckBox, "checkbox"),
        (ControlType.RadioButton, "radio"),
        (ControlType.ComboBox, "combobox"),
        (ControlType.List, "list"),
        (ControlType.ListItem, "listitem"),
        (ControlType.Menu, "menu"),
        (ControlType.MenuItem, "menuitem"),
        (ControlType.MenuBar, "menubar"),
        (ControlType.Tree, "tree"),
        (ControlType.TreeItem, "treeitem"),
        (ControlType.Tab, "tablist"),
        (ControlType.TabItem, "tab"),
        (ControlType.Table, "table"),
        (ControlType.DataItem, "row"),
        (ControlType.Header, "header"),
        (ControlType.HeaderItem, "columnheader"),
        (ControlType.Slider, "slider"),
        (ControlType.Spinner, "spinbutton"),
        (ControlType.ProgressBar, "progressbar"),
        (ControlType.Hyperlink, "link"),
        (ControlType.Image, "image"),
        (ControlType.Pane, "group"),
        (ControlType.Group, "group"),
        (ControlType.Window, "window"),
        (ControlType.Document, "document"),
        (ControlType.ToolBar, "toolbar"),
        (ControlType.ToolTip, "tooltip"),
        (ControlType.ScrollBar, "scrollbar"),
        (ControlType.StatusBar, "status"),
        (ControlType.Separator, "separator"),
        (ControlType.Thumb, "thumb"),
        (ControlType.TitleBar, "titlebar"),
        (ControlType.DataGrid, "grid"),
        (ControlType.Custom, "custom"),
    };

    private static readonly Dictionary<ControlType, string> Roles = RoleTable.ToDictionary(r => r.Type, r => r.Role);

    public static string Role(ControlType type) => Roles.TryGetValue(type, out var role) ? role : "element";

    /// <summary>
    /// Control types a role name (as printed in snapshots) can mean. "group" is both Pane and
    /// Group. Also accepts UIA control type names ("Pane", "DataItem"). Empty if unknown.
    /// </summary>
    public static IReadOnlyList<ControlType> ControlTypesForRole(string role)
    {
        var byRole = RoleTable.Where(r => string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Type).ToList();
        if (byRole.Count > 0) return byRole;
        return Enum.TryParse<ControlType>(role, ignoreCase: true, out var type) && Enum.IsDefined(type)
            ? new[] { type }
            : Array.Empty<ControlType>();
    }

    public static IEnumerable<string> KnownRoles => RoleTable.Select(r => r.Role).Distinct();

    /// <summary>The Name, else "[AutomationId]" when that is short, else null.</summary>
    public static string? DisplayName(string? name, string? automationId)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (!string.IsNullOrWhiteSpace(automationId) && automationId.Length < 50) return $"[{automationId}]";
        return null;
    }

    public static List<string> States(SnapshotNode node)
    {
        var states = new List<string>();
        if (!node.IsEnabled) states.Add("disabled");
        if (node.IsOffscreen) states.Add("offscreen");
        if (node.IsReadOnly) states.Add("readonly");
        if (node.Toggle == ToggleState.On) states.Add("checked");
        else if (node.Toggle == ToggleState.Indeterminate) states.Add("indeterminate");
        if (node.IsSelected) states.Add("selected");
        if (node.ExpandCollapse == ExpandCollapseState.Expanded) states.Add("expanded");
        else if (node.ExpandCollapse == ExpandCollapseState.Collapsed) states.Add("collapsed");
        return states;
    }

    /// <summary>Unnamed decorative elements are left out, along with their children.</summary>
    public static bool ShouldSkip(string? displayName, string role)
    {
        if (!string.IsNullOrEmpty(displayName)) return false;
        return role is "element" or "thumb" or "scrollbar" or "separator" or "titlebar";
    }

    public static string Line(string refId, string? displayName, string role, IEnumerable<string> states)
    {
        var sb = new StringBuilder(role);
        if (!string.IsNullOrEmpty(displayName)) sb.Append(" \"").Append(Escape(displayName)).Append('"');
        sb.Append(" [ref=").Append(refId).Append(']');
        foreach (var s in states) sb.Append(" [").Append(s).Append(']');
        return sb.ToString();
    }

    public static string Indent(int depth) => new(' ', depth * 2);

    public static string Escape(string name) => name
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "");

    public static string TruncationNote(int nodes, string reason) =>
        $"... (snapshot truncated after {nodes} elements: {reason}. Call windows_snapshot with the window handle for the full tree.)";
}
