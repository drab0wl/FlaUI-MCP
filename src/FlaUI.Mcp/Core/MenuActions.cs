using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Batch;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Opens a menu path ("File > Save As...") in one action. Menus are awkward to drive a step at
/// a time: sub-menus and context menus open as separate short-lived popup windows, and they
/// close again when focus moves.
/// </summary>
public sealed class MenuActions
{
    private static readonly TimeSpan AppearTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly SessionManager _sessions;
    private readonly ElementRegistry _elements;
    private readonly ClickExecutor _clicks;

    public MenuActions(SessionManager sessions, ElementRegistry elements, ClickExecutor clicks)
    {
        _sessions = sessions;
        _elements = elements;
        _clicks = clicks;
    }

    /// <param name="windowHandle">The window whose menu bar holds the first item (or that owns the context menu).</param>
    /// <param name="contextTarget">Right-click this element first and walk its context menu.</param>
    /// <param name="mode">auto: expand/invoke patterns; input: mouse clicks on each item.</param>
    /// <param name="lastOptions">For the final item's click (it may open a dialog).</param>
    public async Task<ClickResult> OpenAsync(
        string windowHandle,
        AutomationElement window,
        IReadOnlyList<string> path,
        ClickMode mode,
        (AutomationElement Element, string Ref)? contextTarget = null,
        ActionRunOptions? lastOptions = null)
    {
        if (path.Count == 0) return Error("path is empty");
        var pid = NativeMethods.GetProcessId(_sessions.GetHwnd(windowHandle));
        if (pid == 0) pid = Try(() => window.Properties.ProcessId.ValueOrDefault);
        var trail = new List<string>();
        var opened = new List<AutomationElement>();

        if (contextTarget is { } target)
        {
            var right = await _clicks.ClickAsync(target.Element, new ClickRequest(
                target.Ref, ClickMode.Input, Button: "right", Options: new ActionRunOptions { SettleTime = TimeSpan.Zero }));
            if (right.IsError) return right;
            trail.Add($"right-clicked {target.Ref}");
        }

        AutomationElement? parent = null;
        for (var i = 0; i < path.Count; i++)
        {
            var name = path[i];
            var isFirst = i == 0 && contextTarget == null;
            var item = await WaitForItemAsync(window, parent, pid, name, isFirst);
            if (item == null)
            {
                var available = VisibleItemNames(window, parent, pid, isFirst);
                Close(opened);
                return Error($"No menu item \"{name}\" at {(trail.Count > 0 ? MenuPath.Describe(path.Take(i).ToList()) : "the top level")}." +
                             (available.Count > 0 ? $" Items there: {string.Join(", ", available.Select(n => $"\"{n}\""))}." : ""));
            }

            string? key = null;
            try { key = SnapshotBuilder.RuntimeIdKey(item.Properties.RuntimeId.ValueOrDefault); } catch { }
            var itemRef = _elements.RegisterFound(windowHandle, item, key);
            var itemName = Try(() => item.Properties.Name.ValueOrDefault) ?? name;

            if (i == path.Count - 1)
            {
                // The command itself: this is where a dialog may open.
                var click = await _clicks.ClickAsync(item, new ClickRequest(itemRef, mode, Options: lastOptions));
                return click with { Text = $"{MenuPath.Describe(path)}: {click.Text}" };
            }

            var open = await OpenSubmenuAsync(item, itemRef, itemName, mode);
            if (open.IsError)
            {
                Close(opened);
                return open with { Text = $"Could not open \"{itemName}\": {open.Text}" };
            }
            opened.Add(item);
            trail.Add(itemName);
            parent = item;
        }
        return Error("unreachable");
    }

    private async Task<ClickResult> OpenSubmenuAsync(AutomationElement item, string itemRef, string name, ClickMode mode)
    {
        var quick = new ActionRunOptions { SettleTime = TimeSpan.Zero, WaitTimeout = TimeSpan.FromSeconds(3) };
        if (mode == ClickMode.Input)
        {
            return await _clicks.ClickAsync(item, new ClickRequest(itemRef, ClickMode.Input, Options: quick));
        }
        if (Try(() => item.Patterns.ExpandCollapse.IsSupported))
        {
            var result = await _clicks.RunAsync(item, itemRef, "open menu", _ =>
            {
                var pattern = item.Patterns.ExpandCollapse.Pattern;
                if (pattern.ExpandCollapseState.ValueOrDefault != ExpandCollapseState.Expanded) pattern.Expand();
                return $"Opened {name}";
            }, quick);
            // A menu loop that keeps the call busy still opened the menu.
            return result.StillRunning && !result.DialogOpened ? result with { IsError = false } : result;
        }
        return await _clicks.ClickAsync(item, new ClickRequest(itemRef, ClickMode.Auto, Options: quick));
    }

    private static async Task<AutomationElement?> WaitForItemAsync(AutomationElement window, AutomationElement? parent, int pid, string name, bool topLevel)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var item = NameMatch.Pick(Candidates(window, parent, pid, topLevel), name, e => Try(() => e.Properties.Name.ValueOrDefault));
            if (item != null || sw.Elapsed >= AppearTimeout) return item;
            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Menu items that could be next: under the item just opened (WPF, many WinForms menus),
    /// in the app's popup menus (Win32 and context menus are top-level windows), or anywhere
    /// in the window (menu bars).
    /// </summary>
    private static IReadOnlyList<AutomationElement> Candidates(AutomationElement window, AutomationElement? parent, int pid, bool topLevel)
    {
        var cf = window.ConditionFactory;
        var menuItem = cf.ByControlType(ControlType.MenuItem);
        var result = new List<AutomationElement>();

        if (parent != null)
        {
            result.AddRange(Try(() => parent.FindAllDescendants(menuItem)) ?? Array.Empty<AutomationElement>());
        }
        if (!topLevel)
        {
            var popups = Try(() => window.Automation.GetDesktop().FindAllChildren(
                cf.ByControlType(ControlType.Menu).And(cf.ByProcessId(pid)))) ?? Array.Empty<AutomationElement>();
            foreach (var popup in popups)
            {
                result.AddRange(Try(() => popup.FindAllDescendants(menuItem)) ?? Array.Empty<AutomationElement>());
            }
        }
        if (topLevel || result.Count == 0)
        {
            result.AddRange(Try(() => window.FindAllDescendants(menuItem)) ?? Array.Empty<AutomationElement>());
        }
        return result;
    }

    private static IReadOnlyList<string> VisibleItemNames(AutomationElement window, AutomationElement? parent, int pid, bool topLevel) =>
        Candidates(window, parent, pid, topLevel)
            .Where(e => !Try(() => e.Properties.IsOffscreen.ValueOrDefault))
            .Select(e => Try(() => e.Properties.Name.ValueOrDefault))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct()
            .Take(30)
            .ToList();

    /// <summary>Close sub-menus opened on the way, innermost first, after a failure.</summary>
    private static void Close(List<AutomationElement> opened)
    {
        for (var i = opened.Count - 1; i >= 0; i--)
        {
            try
            {
                if (opened[i].Patterns.ExpandCollapse.TryGetPattern(out var p)) p.Collapse();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static ClickResult Error(string text) => new(text, true, false, false);

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }
}
