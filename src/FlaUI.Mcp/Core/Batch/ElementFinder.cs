using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Core.Snapshots;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core.Batch;

public sealed record FoundElement(AutomationElement Element, string Ref, string WindowHandle);

/// <summary>A search match described like a snapshot line, with where it sits.</summary>
/// <param name="Path">Named ancestors, outermost first (the window excluded).</param>
public sealed record FoundLine(string Ref, string WindowHandle, string Line, IReadOnlyList<string> Path);

/// <summary>
/// Resolves <see cref="ElementSelector"/>s to elements with a UIA search, and registers the
/// match so it gets a ref like any snapshotted element.
/// </summary>
public sealed class ElementFinder
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1000);

    private readonly SessionManager _sessions;
    private readonly ElementRegistry _elements;
    private readonly DialogMonitor _dialogs;
    private readonly PendingOperationRegistry _pending;

    public ElementFinder(SessionManager sessions, ElementRegistry elements, DialogMonitor dialogs, PendingOperationRegistry pending)
    {
        _sessions = sessions;
        _elements = elements;
        _dialogs = dialogs;
        _pending = pending;
    }

    /// <param name="lastDialog">What "$dialog" means right now (a handle), if anything.</param>
    /// <returns>The element, or null with <paramref name="error"/> saying why.</returns>
    public FoundElement? Find(ElementSelector selector, string? lastDialog, out string? error)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return FindCore(selector, lastDialog, out error);
        }
        finally
        {
            TimingLog.Shared.Detail("find", sw.Elapsed, selector.Describe());
        }
    }

    private FoundElement? FindCore(ElementSelector selector, string? lastDialog, out string? error)
    {
        error = null;
        var scopes = Scopes(selector, lastDialog, out error);
        if (scopes == null) return null;

        foreach (var handle in scopes)
        {
            var hwnd = _sessions.GetHwnd(handle);
            if (hwnd != 0 && _pending.HasRunningFor(NativeMethods.GetProcessId(hwnd))
                && !Responsiveness.Probe(() => _ = _sessions.Automation.FromHandle(hwnd).Properties.Name.ValueOrDefault, ProbeTimeout))
            {
                error = $"UI Automation for {handle} is blocked by a pending click. Use dialog_press / dialog_set_text " +
                        "(Win32) on the dialog instead.";
                if (selector.Handle != null) return null;
                continue;
            }

            var window = _sessions.GetWindow(handle);
            if (window == null) continue;

            var element = Search(window, selector);
            if (element == null) continue;

            string? key = null;
            try { key = SnapshotBuilder.RuntimeIdKey(element.Properties.RuntimeId.ValueOrDefault); } catch { }
            var refId = _elements.RegisterFound(handle, element, key);
            error = null;
            return new FoundElement(element, refId, handle);
        }

        error ??= $"No element matches {selector.Describe()}" +
                  (selector.Handle == null ? $" (searched {string.Join(", ", scopes)})" : "") + ".";
        return null;
    }

    /// <summary>
    /// Every match (up to <paramref name="limit"/>) across the selector's windows, each read in
    /// one cached call, with its named ancestors.
    /// </summary>
    /// <param name="more">True when there were more matches than <paramref name="limit"/>.</param>
    public IReadOnlyList<FoundLine> FindAll(ElementSelector selector, int limit, out bool more, out string? error)
    {
        var sw = Stopwatch.StartNew();
        var found = new List<FoundLine>();
        more = false;
        var scopes = Scopes(selector, lastDialog: null, out error);
        if (scopes == null) return found;

        foreach (var handle in scopes)
        {
            var hwnd = _sessions.GetHwnd(handle);
            if (hwnd != 0 && _pending.HasRunningFor(NativeMethods.GetProcessId(hwnd))
                && !Responsiveness.Probe(() => _ = _sessions.Automation.FromHandle(hwnd).Properties.Name.ValueOrDefault, ProbeTimeout))
            {
                error = $"UI Automation for {handle} is blocked by a pending click; skipped it. Use windows_dialog for the dialog.";
                continue;
            }
            var window = _sessions.GetWindow(handle);
            if (window == null) continue;

            var automation = window.Automation;
            var walker = automation.TreeWalkerFactory.GetRawViewWalker();
            // Read before the cache is active (they aren't in any cache).
            var stopAt = new HashSet<string?>
            {
                SnapshotBuilder.RuntimeIdKey(Try(() => window.Properties.RuntimeId.ValueOrDefault)),
                SnapshotBuilder.RuntimeIdKey(Try(() => automation.GetDesktop().Properties.RuntimeId.ValueOrDefault)),
            };
            stopAt.Remove(null);
            using (SnapshotBuilder.CreateCacheRequest(automation, TreeScope.Element).Activate())
            {
                // One call returns the matches with their properties; limit + 1 tells us if there are more.
                var matches = SearchAll(window, selector, limit - found.Count + 1);
                if (matches.Count > limit - found.Count)
                {
                    more = true;
                    matches = matches.Take(limit - found.Count).ToList();
                }

                foreach (var element in matches)
                {
                    var node = SnapshotBuilder.ReadCached(element, automation);
                    var refId = _elements.RegisterFound(handle, element, node.RuntimeId);
                    var name = SnapshotFormat.DisplayName(node.Name, node.AutomationId);
                    var line = SnapshotFormat.Line(refId, name, SnapshotFormat.Role(node.ControlType), SnapshotFormat.States(node));
                    found.Add(new FoundLine(refId, handle, line, Ancestors(walker, element, automation, stopAt)));
                }
            }
            if (found.Count >= limit) break;
        }

        TimingLog.Shared.Detail("find-all", sw.Elapsed, $"{selector.Describe()} matches={found.Count}");
        if (found.Count > 0) error = null;
        else error ??= $"No element matches {selector.Describe()}" +
                       (selector.Handle == null ? $" (searched {string.Join(", ", scopes)})" : "") + ".";
        return found;
    }

    /// <param name="stopAt">Runtime ids of the searched window and the desktop.</param>
    private static IReadOnlyList<string> Ancestors(ITreeWalker walker, AutomationElement element, AutomationBase automation, IReadOnlySet<string?> stopAt)
    {
        const int MaxLevels = 12;
        var path = new List<string>();
        var current = element;
        for (var level = 0; level < MaxLevels; level++)
        {
            AutomationElement? parent;
            try { parent = walker.GetParent(current); } catch { break; }
            if (parent == null) break;
            var node = SnapshotBuilder.ReadCached(parent, automation);
            if (node.RuntimeId == null || stopAt.Contains(node.RuntimeId)) break;
            var name = SnapshotFormat.DisplayName(node.Name, node.AutomationId);
            if (name != null) path.Insert(0, $"{SnapshotFormat.Role(node.ControlType)} \"{SnapshotFormat.Escape(name)}\"");
            current = parent;
        }
        return path;
    }

    /// <summary>Windows to search, most likely first.</summary>
    private IReadOnlyList<string>? Scopes(ElementSelector selector, string? lastDialog, out string? error)
    {
        error = null;
        if (selector.Handle != null)
        {
            var handle = selector.Handle == ElementSelector.LastDialog ? lastDialog : selector.Handle;
            if (handle == null)
            {
                error = "$dialog used before any dialog opened in this batch.";
                return null;
            }
            if (_sessions.GetHwnd(handle) == 0 && _sessions.GetWindow(handle) == null)
            {
                error = $"Unknown window handle {handle}.";
                return null;
            }
            return new[] { handle };
        }

        var pids = _sessions.TrackedProcessIds;
        if (pids.Count == 0)
        {
            error = "No apps tracked yet: give the selector a window handle, or launch/snapshot the app first.";
            return null;
        }

        var ordered = new List<nint>();
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != 0 && pids.Contains(NativeMethods.GetProcessId(foreground))) ordered.Add(foreground);

        // Newest enabled dialogs next (EnumWindows lists top of the z-order first).
        ordered.AddRange(_dialogs.GetDialogs(pids).Where(d => d.IsEnabled).Select(d => d.Hwnd));
        ordered.AddRange(_dialogs.EnumerateTopLevel(pids)
            .Where(w => w.IsVisible && w.Owner == 0 && !string.IsNullOrEmpty(w.Title))
            .Select(w => w.Hwnd));

        return ordered.Distinct()
            .Select(h => _sessions.RegisterNativeWindow(h, NativeMethods.GetProcessId(h)))
            .ToList();
    }

    private static AutomationElement? Search(AutomationElement window, ElementSelector selector)
    {
        if (selector.Index == 0 && selector.NameContains == null)
        {
            // The window itself can match (e.g. role=window), like a snapshot's first line.
            return window.FindFirst(TreeScope.Subtree, Combine(Conditions(window, selector)));
        }
        var all = SearchAll(window, selector, selector.Index + 1);
        return selector.Index < all.Count ? all[selector.Index] : null;
    }

    private static List<ConditionBase> Conditions(AutomationElement window, ElementSelector selector)
    {
        var cf = window.ConditionFactory;
        var conditions = new List<ConditionBase>();
        if (selector.Name != null) conditions.Add(cf.ByName(selector.Name));
        if (selector.AutomationId != null) conditions.Add(cf.ByAutomationId(selector.AutomationId));
        if (selector.Role != null)
        {
            var types = SnapshotFormat.ControlTypesForRole(selector.Role);
            conditions.Add(types.Count == 1
                ? cf.ByControlType(types[0])
                : new OrCondition(types.Select(t => (ConditionBase)cf.ByControlType(t))));
        }
        return conditions;
    }

    /// <summary>Matches in document order, at most <paramref name="max"/>.</summary>
    private static List<AutomationElement> SearchAll(AutomationElement window, ElementSelector selector, int max)
    {
        var conditions = Conditions(window, selector);
        if (selector.NameContains != null)
        {
            try
            {
                var withSubstring = conditions.Append(window.ConditionFactory.ByName(selector.NameContains, PropertyConditionFlags.MatchSubstring)).ToList();
                return window.FindAll(TreeScope.Subtree, Combine(withSubstring)).Take(max).ToList();
            }
            catch
            {
                // Substring conditions need Windows 10 1809+: filter by hand instead.
                var candidates = conditions.Count == 0
                    ? window.FindAll(TreeScope.Subtree, TrueCondition.Default)
                    : window.FindAll(TreeScope.Subtree, Combine(conditions));
                return candidates
                    .Where(e => (Try(() => e.Properties.Name.ValueOrDefault) ?? "")
                        .Contains(selector.NameContains, StringComparison.OrdinalIgnoreCase))
                    .Take(max)
                    .ToList();
            }
        }
        return window.FindAll(TreeScope.Subtree, Combine(conditions)).Take(max).ToList();
    }

    private static ConditionBase Combine(IReadOnlyList<ConditionBase> conditions) =>
        conditions.Count == 1 ? conditions[0] : new AndCondition(conditions);

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }
}
