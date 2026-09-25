using System.Diagnostics;
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

        if (selector.NameContains != null)
        {
            try
            {
                var withSubstring = conditions.Append(cf.ByName(selector.NameContains, PropertyConditionFlags.MatchSubstring)).ToList();
                return Pick(window, Combine(withSubstring), selector.Index);
            }
            catch
            {
                // Substring conditions need Windows 10 1809+: filter by hand instead.
                var candidates = conditions.Count == 0
                    ? window.FindAllDescendants()
                    : window.FindAllDescendants(Combine(conditions));
                return candidates
                    .Where(e => (Try(() => e.Properties.Name.ValueOrDefault) ?? "")
                        .Contains(selector.NameContains, StringComparison.OrdinalIgnoreCase))
                    .Skip(selector.Index)
                    .FirstOrDefault();
            }
        }

        return Pick(window, Combine(conditions), selector.Index);
    }

    private static ConditionBase Combine(IReadOnlyList<ConditionBase> conditions) =>
        conditions.Count == 1 ? conditions[0] : new AndCondition(conditions);

    private static AutomationElement? Pick(AutomationElement window, ConditionBase condition, int index)
    {
        if (index == 0)
        {
            // The window itself can match (e.g. role=window), like a snapshot's first line.
            return window.FindFirst(TreeScope.Subtree, condition);
        }
        var all = window.FindAll(TreeScope.Subtree, condition);
        return index < all.Length ? all[index] : null;
    }

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }
}
