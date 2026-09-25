using System.Text;
using FlaUI.Core.Definitions;

namespace PlaywrightWindows.Mcp.Core.Tables;

/// <summary>What the table reader needs to know about one element. Pure data.</summary>
public sealed record TableNode(ControlType Type, string? Name, string? Value, bool HasChildren = true);

/// <summary>
/// How grids, list views and data grids lay out their UI Automation trees, as pure rules:
/// a header (Header, or a row of HeaderItems), then rows (DataItem / ListItem / TreeItem /
/// Custom) whose children are the cells.
/// </summary>
public static class TableLayout
{
    private static readonly HashSet<ControlType> RowTypes = new()
    {
        ControlType.DataItem, ControlType.ListItem, ControlType.TreeItem, ControlType.Custom,
    };

    private static readonly HashSet<ControlType> Chrome = new()
    {
        ControlType.ScrollBar, ControlType.Thumb, ControlType.Button, ControlType.TitleBar, ControlType.Separator,
    };

    public enum Kind { Other, Header, HeaderRow, Row, Container }

    /// <param name="childTypes">The element's children's types, when known (to spot header rows).</param>
    public static Kind Classify(TableNode node, IReadOnlyList<ControlType>? childTypes = null)
    {
        if (node.Type == ControlType.Header) return Kind.Header;
        if (Chrome.Contains(node.Type)) return Kind.Other;
        if (childTypes is { Count: > 0 } && childTypes.All(t => t is ControlType.HeaderItem or ControlType.Header))
        {
            return Kind.HeaderRow;
        }
        if (RowTypes.Contains(node.Type)) return Kind.Row;
        // WPF puts rows inside a presenter; some grids group them in panes.
        if (node.Type is ControlType.Pane or ControlType.Group or ControlType.Table or ControlType.List or ControlType.DataGrid
            && node.HasChildren)
        {
            return Kind.Container;
        }
        return Kind.Other;
    }

    /// <summary>Column names from a header's HeaderItems.</summary>
    public static List<string> HeaderNames(IEnumerable<TableNode> headerItems) =>
        headerItems.Where(h => h.Type == ControlType.HeaderItem).Select(h => Clean(h.Name)).ToList();

    /// <summary>A row's cell texts: its children minus row-header cells, or the row's own name when it has none.</summary>
    public static List<string> Cells(TableNode row, IReadOnlyList<TableNode> children)
    {
        var cells = children
            .Where(c => c.Type is not (ControlType.HeaderItem or ControlType.Header) && !Chrome.Contains(c.Type))
            .Select(Text)
            .ToList();
        return cells.Count > 0 ? cells : new List<string> { Text(row) };
    }

    /// <summary>A cell's text: its value when it has one, else its name.</summary>
    public static string Text(TableNode node) => Clean(!string.IsNullOrEmpty(node.Value) ? node.Value : node.Name);

    /// <summary>
    /// Drop an empty leading header (the corner above row headers) when headers have one more
    /// column than the rows.
    /// </summary>
    public static List<string> Align(List<string> headers, int cellCount)
    {
        if (headers.Count == cellCount + 1 && headers[0].Length == 0) return headers.Skip(1).ToList();
        return headers;
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace("\t", " ").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");

    /// <param name="start">0-based index of the first row shown.</param>
    /// <param name="available">Rows the control exposes right now.</param>
    /// <param name="total">All rows the control has, if it said (may be more than it exposes).</param>
    public static string Render(string title, IReadOnlyList<string> headers, IReadOnlyList<(string Ref, IReadOnlyList<string> Cells)> rows,
        int start, int available, int? total, bool more)
    {
        var sb = new StringBuilder();
        var range = rows.Count == 0 ? "no rows" : $"rows {start + 1}-{start + rows.Count} of {available}";
        sb.Append(title).Append(": ").Append(range);
        if (total is { } t && t > available) sb.Append($" ({t} in total; the rest aren't loaded: scroll, or select/find by name)");
        sb.AppendLine();
        if (headers.Count > 0) sb.Append("ref\t").AppendLine(string.Join("\t", headers));
        foreach (var (refId, cells) in rows) sb.Append(refId).Append('\t').AppendLine(string.Join("\t", cells));
        if (more) sb.AppendLine($"... more rows: call again with start={start + rows.Count}.");
        return sb.ToString().TrimEnd();
    }
}
