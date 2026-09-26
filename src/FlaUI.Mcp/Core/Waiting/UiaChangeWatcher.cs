using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Diagnostics;

namespace PlaywrightWindows.Mcp.Core.Waiting;

/// <summary>
/// Subscribes to UI Automation events for the duration of a wait, so the wait re-checks the
/// moment something changes instead of on a timer: windows opening anywhere on the desktop (dialogs)
/// or closing, and structure or name/value/state changes inside the watched window. Anything that fails to
/// subscribe is skipped; waits still fall back to polling.
/// </summary>
public sealed class UiaChangeWatcher : IDisposable
{
    private readonly List<IDisposable> _handlers = new();

    private UiaChangeWatcher()
    {
    }

    public ChangeSignal Signal { get; } = new();

    /// <summary>How many subscriptions took; 0 means the wait is plain polling.</summary>
    public int Subscriptions => _handlers.Count;

    /// <param name="window">Watch inside this window too (null: only new windows).</param>
    public static UiaChangeWatcher Start(AutomationBase automation, AutomationElement? window)
    {
        var watcher = new UiaChangeWatcher();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var desktop = automation.GetDesktop();
        watcher.Try(() => desktop.RegisterAutomationEvent(
            automation.EventLibrary.Window.WindowOpenedEvent, TreeScope.Subtree, (_, _) => watcher.Signal.Pulse()));
        watcher.Try(() => desktop.RegisterAutomationEvent(
            automation.EventLibrary.Window.WindowClosedEvent, TreeScope.Subtree, (_, _) => watcher.Signal.Pulse()));

        if (window != null)
        {
            watcher.Try(() => window.RegisterStructureChangedEvent(TreeScope.Subtree, (_, _, _) => watcher.Signal.Pulse()));
            var p = automation.PropertyLibrary;
            watcher.Try(() => window.RegisterPropertyChangedEvent(TreeScope.Subtree, (_, _, _) => watcher.Signal.Pulse(),
                p.Element.Name, p.Element.IsEnabled, p.Value.Value, p.Toggle.ToggleState,
                p.ExpandCollapse.ExpandCollapseState, p.SelectionItem.IsSelected, p.RangeValue.Value));
        }

        TimingLog.Shared.Detail("watch", sw.Elapsed, $"subscriptions={watcher.Subscriptions}");
        return watcher;
    }

    private void Try(Func<IDisposable> register)
    {
        try
        {
            _handlers.Add(register());
        }
        catch
        {
            // Some providers don't support events; polling covers them.
        }
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            try { handler.Dispose(); } catch { }
        }
        _handlers.Clear();
    }
}
