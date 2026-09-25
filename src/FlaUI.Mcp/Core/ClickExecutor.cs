using System.Drawing;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core;

public enum ClickMode
{
    /// <summary>UIA pattern (Invoke/Toggle/Select) when supported, otherwise a real mouse click.</summary>
    Auto,
    /// <summary>UIA pattern only. Works with the window in the background.</summary>
    Invoke,
    /// <summary>
    /// Real mouse input at the element's clickable point. Never leaves a UIA call outstanding,
    /// so a dialog opened by the click can be snapshotted and driven normally. Needs the window
    /// in the foreground and an unlocked session.
    /// </summary>
    Input,
}

public sealed record ClickRequest(
    string Ref,
    ClickMode Mode = ClickMode.Auto,
    string Button = "left",
    bool DoubleClick = false,
    ActionRunOptions? Options = null);

public sealed record ClickResult(string Text, bool IsError, bool DialogOpened, bool StillRunning)
{
    /// <summary>Process the click went to (0 if it never got that far).</summary>
    public int ProcessId { get; init; }

    /// <summary>Top-level window of the clicked element, when known from its ref.</summary>
    public nint WindowHwnd { get; init; }

    /// <summary>Dialogs the click opened.</summary>
    public IReadOnlyList<DialogInfo> NewDialogs { get; init; } = Array.Empty<DialogInfo>();
}

/// <summary>
/// Performs clicks without ever hanging on a modal dialog: the UIA call runs on its own
/// thread while the Win32 dialog monitor watches for new dialogs in the target process.
/// </summary>
public sealed class ClickExecutor
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly SessionManager _sessions;
    private readonly DialogMonitor _dialogs;
    private readonly ActionRunner _runner;
    private readonly PendingOperationRegistry _pending;

    public ClickExecutor(SessionManager sessions, DialogMonitor dialogs, ActionRunner runner, PendingOperationRegistry pending)
    {
        _sessions = sessions;
        _dialogs = dialogs;
        _runner = runner;
        _pending = pending;
    }

    public async Task<ClickResult> ClickAsync(AutomationElement element, ClickRequest request)
    {
        // If an earlier action in this process is stuck behind a dialog, the provider may not
        // answer. Find out in 1.5s rather than hanging for the UIA default of 20s.
        var pid = SafeGet(() => element.Properties.ProcessId.ValueOrDefault, 0, ProbeTimeout, out var answered);
        if (!answered)
        {
            return Blocked(request.Ref);
        }

        _sessions.TrackProcess(pid);
        var name = SafeGet(() => element.Properties.Name.ValueOrDefault, null, ProbeTimeout, out _) ?? request.Ref;

        var plain = request.Button == "left" && !request.DoubleClick;
        var usePattern = request.Mode switch
        {
            ClickMode.Input => false,
            ClickMode.Invoke => true,
            _ => plain && HasActionPattern(element),
        };

        if (usePattern && !plain)
        {
            return new ClickResult("mode=invoke only supports a single left click. Use mode=input for right/double clicks.", true, false, false);
        }

        var windowHwnd = WindowHwndForRef(request.Ref);
        Func<string> action = usePattern
            ? () => InvokeByPattern(element, name)
            : () => ClickWithInput(element, name, pid, windowHwnd, request);

        var pids = new HashSet<int> { pid };
        var baseline = _dialogs.GetDialogs(pids);
        var description = $"click \"{name}\" ({(usePattern ? "pattern" : "input")})";

        var outcome = await _runner.RunAsync(
            description,
            pid,
            action,
            () => DialogClassifier.NewSince(baseline, _dialogs.GetDialogs(pids)),
            request.Options);

        return Format(outcome) with { ProcessId = pid, WindowHwnd = windowHwnd, NewDialogs = outcome.NewDialogs };
    }

    /// <summary>
    /// Runs any UI Automation action on an element the way clicks run: on its own thread, raced
    /// against dialog detection, so a pattern call that opens a modal dialog returns right away.
    /// </summary>
    /// <param name="verb">For messages: "expand", "select \"Blue\" in", ...</param>
    /// <param name="action">Gets the element's name; returns the result text or throws.</param>
    public async Task<ClickResult> RunAsync(AutomationElement element, string refId, string verb, Func<string, string> action, ActionRunOptions? options = null)
    {
        var pid = SafeGet(() => element.Properties.ProcessId.ValueOrDefault, 0, ProbeTimeout, out var answered);
        if (!answered)
        {
            return Blocked(refId);
        }

        _sessions.TrackProcess(pid);
        var name = SafeGet(() => element.Properties.Name.ValueOrDefault, null, ProbeTimeout, out _) ?? refId;
        if (string.IsNullOrWhiteSpace(name)) name = refId;
        var windowHwnd = WindowHwndForRef(refId);

        var pids = new HashSet<int> { pid };
        var baseline = _dialogs.GetDialogs(pids);
        var outcome = await _runner.RunAsync(
            $"{verb} \"{name}\"",
            pid,
            () => action(name),
            () => DialogClassifier.NewSince(baseline, _dialogs.GetDialogs(pids)),
            options);

        return Format(outcome, "The action opened") with { ProcessId = pid, WindowHwnd = windowHwnd, NewDialogs = outcome.NewDialogs };
    }

    private static bool HasActionPattern(AutomationElement element) =>
        element.Patterns.Invoke.IsSupported
        || element.Patterns.Toggle.IsSupported
        || element.Patterns.SelectionItem.IsSupported;

    private static string InvokeByPattern(AutomationElement element, string name)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return $"Invoked {name}";
        }
        if (element.Patterns.Toggle.IsSupported)
        {
            element.Patterns.Toggle.Pattern.Toggle();
            var state = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
            return $"Toggled {name} to {state}";
        }
        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
            return $"Selected {name}";
        }
        throw new InvalidOperationException(
            $"{name} supports no Invoke, Toggle or SelectionItem pattern. Use mode=input for a mouse click.");
    }

    private nint WindowHwndForRef(string refId) =>
        ElementRegistry.WindowHandleOf(refId) is { } handle ? _sessions.GetHwnd(handle) : 0;

    private static string ClickWithInput(AutomationElement element, string name, int pid, nint windowHwnd, ClickRequest request)
    {
        var point = ResolvePoint(element);

        // Bring the element's own top-level window forward (known from its ref), or failing
        // that whatever window of the same app is at the point.
        var target = windowHwnd != 0 && NativeMethods.IsWindow(windowHwnd) && NativeMethods.GetProcessId(windowHwnd) == pid
            ? windowHwnd
            : NativeMethods.RootOf(HwndAt(point));
        if (target == 0 || NativeMethods.GetProcessId(target) != pid)
        {
            throw new InvalidOperationException(
                $"Could not find {name}'s window: the click point ({point.X},{point.Y}) is covered by another app " +
                $"(\"{NativeMethods.GetCaption(target)}\"). Bring it forward with windows_focus, or use mode=invoke.");
        }

        var foreground = NativeMethods.BringToForeground(target);

        // Never send input into another app: verify what's actually under the point now.
        var under = NativeMethods.RootOf(HwndAt(point));
        if (under == 0 || NativeMethods.GetProcessId(under) != pid)
        {
            throw new InvalidOperationException(
                $"The click point for {name} is covered by another app's window (\"{NativeMethods.GetCaption(under)}\") " +
                "even after bringing the target forward; not clicking. Use mode=invoke, or move the covering window.");
        }

        var mouseButton = request.Button switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        if (request.DoubleClick)
        {
            Mouse.DoubleClick(point, mouseButton);
        }
        else
        {
            Mouse.Click(point, mouseButton);
        }

        var verb = request.DoubleClick ? "Double-clicked" : "Clicked";
        return foreground
            ? $"{verb} {name} (mouse input)"
            : $"{verb} {name} (mouse input; could not confirm the window reached the foreground)";
    }

    private static Point ResolvePoint(AutomationElement element)
    {
        if (element.TryGetClickablePoint(out var point))
        {
            return point;
        }
        var rect = element.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("Element has no clickable point or bounds (it may be offscreen or collapsed).");
        }
        return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    private static nint HwndAt(Point p) => NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = p.X, Y = p.Y });

    private ClickResult Format(ActionOutcome outcome, string openedLead = "The click opened")
    {
        var sb = new StringBuilder();
        switch (outcome.State)
        {
            case ActionState.Completed:
                sb.Append(outcome.Result);
                if (outcome.NewDialogs.Count > 0)
                {
                    sb.AppendLine();
                    AppendDialogs(sb, outcome.NewDialogs, openedLead);
                }
                return new ClickResult(sb.ToString(), false, outcome.NewDialogs.Count > 0, false);

            case ActionState.Faulted:
                sb.Append($"Failed to {outcome.Description}: {outcome.Error?.Message}");
                if (outcome.NewDialogs.Count > 0)
                {
                    sb.AppendLine();
                    AppendDialogs(sb, outcome.NewDialogs, "A dialog is open");
                }
                return new ClickResult(sb.ToString(), true, outcome.NewDialogs.Count > 0, false);

            case ActionState.DialogOpened:
                var op = outcome.Operation!;
                sb.AppendLine($"The {outcome.Description} opened a dialog. The app is waiting for it, so the {Noun(outcome.Description)} is pending as {op.Id}.");
                AppendDialogs(sb, outcome.NewDialogs, "Dialog");
                sb.Append($"{op.Id} finishes when the dialog closes; tool results will report it, or call windows_wait with op={op.Id}.");
                return new ClickResult(sb.ToString(), false, true, true);

            default:
                var pending = outcome.Operation!;
                sb.Append($"The {outcome.Description} has not returned yet ({pending.Id}). The app may be busy. ");
                sb.Append($"Call windows_wait with op={pending.Id}, or windows_dialogs to check for dialogs.");
                return new ClickResult(sb.ToString(), false, false, true);
        }
    }

    private static string Noun(string description) =>
        description.StartsWith("click", StringComparison.Ordinal) ? "click" : "action";

    private void AppendDialogs(StringBuilder sb, IReadOnlyList<DialogInfo> dialogs, string lead)
    {
        foreach (var d in dialogs)
        {
            var handle = _sessions.RegisterNativeWindow(d.Hwnd, d.ProcessId);
            sb.AppendLine($"{lead}: {handle} {DialogClassifier.Describe(d)}");
        }
        var first = _sessions.RegisterNativeWindow(dialogs[0].Hwnd, dialogs[0].ProcessId);
        sb.AppendLine(dialogs[0].IsWin32Dialog
            ? $"Read it with windows_snapshot handle={first}, or windows_dialog handle={first} (Win32, works even if UI Automation is blocked)."
            : $"Read it with windows_snapshot handle={first}.");
    }

    private ClickResult Blocked(string refId)
    {
        var running = _pending.Running();
        var ops = running.Count > 0 ? $" Pending: {string.Join(", ", running.Select(o => $"{o.Id} {o.Description}"))}." : "";
        return new ClickResult(
            $"UI Automation for {refId}'s app is not answering (probably blocked behind a modal dialog).{ops} " +
            "Use windows_dialogs to find the dialog, then windows_dialog to press its buttons.",
            true, false, false);
    }

    /// <summary>
    /// Reads a UIA property on a separate thread so a blocked provider costs at most
    /// <paramref name="timeout"/>.
    /// </summary>
    private static T SafeGet<T>(Func<T> read, T fallback, TimeSpan timeout, out bool answered)
    {
        var task = ActionRunner.StartOnDedicatedThread(read, "property read");
        try
        {
            answered = task.Wait(timeout);
            return answered ? task.Result : fallback;
        }
        catch (AggregateException)
        {
            answered = true;
            return fallback;
        }
    }
}
