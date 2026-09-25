using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using PlaywrightWindows.Mcp.Core.Snapshots;

namespace PlaywrightWindows.Mcp.Core.Tables;

/// <summary>
/// Reads a grid or list as rows and columns. Each level (the grid's children, a row's cells)
/// is one cached UI Automation call.
/// </summary>
public sealed class TableReader
{
    private readonly ElementRegistry _elements;

    public TableReader(ElementRegistry elements)
    {
        _elements = elements;
    }

    public string Read(AutomationElement table, string tableRef, string windowHandle, int start, int maxRows)
    {
        var sw = Stopwatch.StartNew();
        var automation = table.Automation;
        var p = automation.PropertyLibrary;
        int? total = null;
        try { total = table.Patterns.Grid.TryGetPattern(out var grid) ? grid.RowCount.ValueOrDefault : null; } catch { }
        var title = $"{SnapshotFormat.Role(Try(() => table.Properties.ControlType.ValueOrDefault))} " +
                    $"\"{SnapshotFormat.Escape(Try(() => table.Properties.Name.ValueOrDefault) ?? "")}\" [ref={tableRef}]";

        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (var id in new[] { p.Element.Name, p.Element.ControlType, p.Element.RuntimeId, p.Value.Value, p.Element.IsOffscreen })
        {
            request.Add(id);
        }

        using (request.Activate())
        {
            var headers = new List<string>();
            var rows = new List<AutomationElement>();
            Collect(table, automation, headers, rows, depth: 0);

            var shown = new List<(string Ref, IReadOnlyList<string> Cells)>();
            foreach (var row in rows.Skip(start).Take(maxRows))
            {
                var node = Node(row, automation);
                var cells = TableLayout.Cells(node, Children(row).Select(c => Node(c, automation)).ToList());
                var key = SnapshotBuilder.RuntimeIdKey(Get<int[]>(row, p.Element.RuntimeId));
                shown.Add((_elements.RegisterFound(windowHandle, row, key), cells));
            }

            if (shown.Count > 0) headers = TableLayout.Align(headers, shown[0].Cells.Count);
            var text = TableLayout.Render(title, headers, shown, start, rows.Count, total, rows.Count > start + shown.Count);
            TimingLog.Shared.Detail("read-table", sw.Elapsed, $"ref={tableRef} rows={shown.Count}/{rows.Count}");
            return text;
        }
    }

    /// <summary>Header names and row elements, looking one level into row containers.</summary>
    private static void Collect(AutomationElement parent, AutomationBase automation, List<string> headers, List<AutomationElement> rows, int depth)
    {
        foreach (var child in Children(parent))
        {
            var node = Node(child, automation);
            var kind = TableLayout.Classify(node);
            if (kind == TableLayout.Kind.Header)
            {
                if (headers.Count == 0) headers.AddRange(TableLayout.HeaderNames(Children(child).Select(c => Node(c, automation))));
                continue;
            }
            if (kind is not (TableLayout.Kind.Row or TableLayout.Kind.Container)) continue;

            // A "row" of header cells (WinForms DataGridView's top row) is the header. It comes
            // before the data, so only look while there are no rows yet (a call per row otherwise).
            if (headers.Count == 0 && rows.Count == 0)
            {
                var cellNodes = Children(child).Select(c => Node(c, automation)).ToList();
                if (TableLayout.Classify(node, cellNodes.Select(c => c.Type).ToList()) == TableLayout.Kind.HeaderRow)
                {
                    headers.AddRange(TableLayout.HeaderNames(cellNodes));
                    continue;
                }
            }
            if (kind == TableLayout.Kind.Container)
            {
                if (depth < 1) Collect(child, automation, headers, rows, depth + 1);
                continue;
            }
            rows.Add(child);
        }
    }

    private static AutomationElement[] Children(AutomationElement element)
    {
        try { return element.FindAllChildren(); } catch { return Array.Empty<AutomationElement>(); }
    }

    private static TableNode Node(AutomationElement e, AutomationBase automation)
    {
        var p = automation.PropertyLibrary;
        return new TableNode(Get(e, p.Element.ControlType, ControlType.Unknown), Get<string>(e, p.Element.Name), Get<string>(e, p.Value.Value));
    }

    private static T? Get<T>(AutomationElement e, PropertyId id, T? fallback = default)
    {
        try { return e.FrameworkAutomationElement.TryGetPropertyValue<T>(id, out var v) ? v : fallback; }
        catch { return fallback; }
    }

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }
}
