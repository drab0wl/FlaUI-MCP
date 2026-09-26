using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core.Waiting;

/// <summary>
/// Waits until an app looks idle: its window answers messages promptly, no progress bar is
/// part-way, and its UI hasn't changed for a while. For after "Build", "Open", "Search"...
/// when there's no particular text to wait for.
/// </summary>
public sealed class IdleWaiter
{
    public static readonly TimeSpan DefaultStable = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProgressCheckEvery = TimeSpan.FromMilliseconds(250);

    private readonly SessionManager _sessions;
    private readonly PendingOperationRegistry _pending;

    public IdleWaiter(SessionManager sessions, PendingOperationRegistry pending)
    {
        _sessions = sessions;
        _pending = pending;
    }

    /// <returns>Whether it went idle, and a description of the last state seen.</returns>
    public async Task<(bool Idle, string State)> WaitAsync(nint hwnd, TimeSpan timeout, TimeSpan stable)
    {
        var pid = NativeMethods.GetProcessId(hwnd);
        var blocked = _pending.HasRunningFor(pid);
        AutomationElement? window = null;
        if (!blocked)
        {
            try { window = _sessions.Automation.FromHandle(hwnd); } catch { }
        }

        using var watcher = UiaChangeWatcher.Start(_sessions.Automation, window);
        // The two desktop subscriptions don't see changes inside the window.
        var watching = window != null && watcher.Subscriptions > 2;
        var started = DateTime.UtcNow;
        var state = "";
        var progress = false;
        var lastProgressCheck = DateTime.MinValue;

        var idle = await Poll.UntilAsync(() =>
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                state = "the window closed";
                return true;
            }
            if (_pending.HasRunningFor(pid))
            {
                state = "an action is still waiting on a dialog";
                return false;
            }

            var responsive = NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_NULL, 0, 0,
                NativeMethods.SMTO_ABORTIFHUNG, 200, out _) != 0;
            // Without events, "quiet" can only mean "time spent waiting".
            var quiet = watching ? watcher.Signal.Quiet : DateTime.UtcNow - started;

            if (responsive && quiet >= stable && window != null && DateTime.UtcNow - lastProgressCheck >= ProgressCheckEvery)
            {
                progress = AnyProgressRunning(window);
                lastProgressCheck = DateTime.UtcNow;
            }
            state = Idle.Describe(responsive, progress, quiet, stable);
            return Idle.IsIdle(responsive, progress, quiet, stable);
        }, timeout, watcher.Signal, TimeSpan.FromMilliseconds(100));

        return (idle, state);
    }

    private static bool AnyProgressRunning(AutomationElement window)
    {
        try
        {
            var automation = window.Automation;
            var p = automation.PropertyLibrary;
            var request = new CacheRequest { TreeScope = TreeScope.Element };
            foreach (var id in new[] { p.Element.IsOffscreen, p.RangeValue.Value, p.RangeValue.Minimum, p.RangeValue.Maximum })
            {
                request.Add(id);
            }
            using (request.Activate())
            {
                foreach (var bar in window.FindAllDescendants(window.ConditionFactory.ByControlType(ControlType.ProgressBar)))
                {
                    var e = bar.FrameworkAutomationElement;
                    if (e.TryGetPropertyValue<bool>(p.Element.IsOffscreen, out var offscreen) && offscreen) continue;
                    if (e.TryGetPropertyValue<double>(p.RangeValue.Value, out var value)
                        && e.TryGetPropertyValue<double>(p.RangeValue.Minimum, out var min)
                        && e.TryGetPropertyValue<double>(p.RangeValue.Maximum, out var max)
                        && Idle.IsRunning(value, min, max))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Can't tell: don't hold the wait up on it.
        }
        return false;
    }
}
