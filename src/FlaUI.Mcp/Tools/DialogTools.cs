using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Core.Waiting;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// List dialogs open in the apps being automated. Uses Win32 only, so it answers even when
/// the app's UI Automation provider is blocked.
/// </summary>
public class DialogsTool : ToolBase
{
    private readonly SessionManager _sessions;
    private readonly DialogMonitor _monitor;
    private readonly PendingOperationRegistry _pending;

    public DialogsTool(SessionManager sessions, DialogMonitor monitor, PendingOperationRegistry pending)
    {
        _sessions = sessions;
        _monitor = monitor;
        _pending = pending;
    }

    public override string Name => "windows_dialogs";

    public override string Description =>
        
        "List dialogs open in the automated apps (handles for windows_snapshot or windows_dialog) and clicks pending behind them. Works while UI Automation is blocked.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            processId = new
            {
                type = "integer",
                description = "Only this process. Default: every app you've launched, listed, or snapshotted."
            },
            allProcesses = new
            {
                type = "boolean",
                description = "Look at every process on the desktop (default: false)"
            }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var pid = GetArgument<int?>(arguments, "processId");
        var all = GetBoolArgument(arguments, "allProcesses", false);

        IReadOnlySet<int>? pids = all ? null
            : pid is > 0 ? new HashSet<int> { pid.Value }
            : _sessions.TrackedProcessIds;

        if (pids is { Count: 0 })
        {
            return Task.FromResult(TextResult(
                "No apps tracked yet. Launch or snapshot an app first, or pass processId / allProcesses=true."));
        }

        var dialogs = _monitor.GetDialogs(pids);
        var sb = new StringBuilder();
        if (dialogs.Count == 0)
        {
            sb.AppendLine("No dialogs open.");
        }
        else
        {
            foreach (var d in dialogs)
            {
                var handle = _sessions.RegisterNativeWindow(d.Hwnd, d.ProcessId, track: !all);
                var owner = d.Owner != 0 ? $", owner {_sessions.RegisterNativeWindow(d.Owner, d.ProcessId, track: !all)}" : "";
                sb.AppendLine($"- {handle} {DialogClassifier.Describe(d)} (pid {d.ProcessId}, class {d.ClassName}{owner})");
            }
        }

        var running = _pending.Running();
        if (running.Count > 0)
        {
            sb.AppendLine("Pending actions:");
            foreach (var op in running)
            {
                sb.AppendLine($"- {op.Id}: {op.Description}");
            }
        }

        return Task.FromResult(TextResult(sb.ToString().TrimEnd()));
    }
}

/// <summary>
/// Read and drive HWND-based dialogs with window messages, bypassing UI Automation.
/// </summary>
public class NativeDialogTool : ToolBase
{
    private readonly SessionManager _sessions;
    private readonly PostActionSnapshotter? _post;
    private readonly DialogMonitor _monitor = new();

    public NativeDialogTool(SessionManager sessions, PostActionSnapshotter? post = null)
    {
        _sessions = sessions;
        _post = post;
    }

    public override string Name => "windows_dialog";

    public override string Description =>
        
        "Read or drive a classic dialog (MessageBox, #32770, WinForms) with window messages; works while UI Automation is blocked. action: read (default; lists controls c1, c2...), press (button text, cN, or ok/cancel/yes/no...), set_text (control + text), close. For WPF and task dialogs use windows_snapshot instead.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new { type = "string", description = "Dialog window handle (from windows_dialogs or a click result)" },
            action = new { type = "string", @enum = new[] { "read", "press", "set_text", "close" }, description = "Default: read" },
            button = new { type = "string", description = "For press: button text, control ref, or standard name" },
            control = new { type = "string", description = "For set_text: control ref from action=read (e.g. 'c4')" },
            text = new { type = "string", description = "For set_text: the text" },
            postSnapshot = new { type = new[] { "boolean", "string" }, description = PostActionModes.SchemaDescription }
        },
        required = new[] { "handle" }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        if (string.IsNullOrEmpty(handle))
        {
            return Task.FromResult(ErrorResult("Missing required argument: handle"));
        }

        var hwnd = _sessions.GetHwnd(handle);
        if (hwnd == 0)
        {
            return Task.FromResult(ErrorResult($"Unknown window handle {handle}. Use windows_dialogs to list dialogs."));
        }
        if (!DialogMonitor.IsOpen(hwnd))
        {
            return Task.FromResult(ErrorResult($"{handle} is no longer open."));
        }

        var action = GetStringArgument(arguments, "action") ?? "read";
        var controls = NativeDialog.ReadControls(hwnd);

        switch (action)
        {
            case "read":
                return Task.FromResult(TextResult(Read(handle, hwnd, controls)));

            case "press":
            {
                var button = GetStringArgument(arguments, "button");
                if (string.IsNullOrWhiteSpace(button))
                {
                    return Task.FromResult(ErrorResult("press needs 'button'"));
                }
                var target = NativeDialog.FindButton(controls, button);
                if (target == null)
                {
                    return Task.FromResult(ErrorResult(
                        $"No button matching \"{button}\" in {handle}.\n{Read(handle, hwnd, controls)}"));
                }
                if (!target.IsEnabled)
                {
                    return Task.FromResult(ErrorResult($"{target.Ref} \"{target.Text}\" is disabled."));
                }
                var pid = NativeMethods.GetProcessId(hwnd);
                var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
                var baseline = _monitor.GetDialogs(new HashSet<int> { pid });
                if (!NativeDialog.Press(target))
                {
                    return Task.FromResult(ErrorResult($"Could not post a click to {target.Ref}."));
                }

                // The press is posted, so give the app a moment before reporting.
                var closed = WaitUntil(() => !DialogMonitor.IsOpen(hwnd), TimeSpan.FromMilliseconds(750));
                var text = $"Pressed {target.Ref} \"{target.Text}\" in {handle}. " +
                           (closed ? "The dialog closed." : "The dialog is still open.");
                if (_post != null && _post.ModeFor(arguments) != PostActionMode.Off)
                {
                    var opened = DialogClassifier.NewSince(baseline, _monitor.GetDialogs(new HashSet<int> { pid }));
                    text = PostActionSnapshotter.Append(text, _post.Capture(
                        new PostActionContext("press", pid, closed ? owner : hwnd, opened), _post.ModeFor(arguments)));
                }
                return Task.FromResult(TextResult(text));
            }

            case "set_text":
            {
                var controlRef = GetStringArgument(arguments, "control");
                var text = GetStringArgument(arguments, "text");
                if (string.IsNullOrEmpty(controlRef) || text == null)
                {
                    return Task.FromResult(ErrorResult("set_text needs 'control' and 'text'"));
                }
                var target = controls.FirstOrDefault(c => string.Equals(c.Ref, controlRef, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    return Task.FromResult(ErrorResult($"No control {controlRef} in {handle}.\n{Read(handle, hwnd, controls)}"));
                }
                if (target.Kind is not (NativeControlKind.Edit or NativeControlKind.ComboBox))
                {
                    return Task.FromResult(ErrorResult($"{controlRef} is a {target.Kind}, not an edit box."));
                }
                return Task.FromResult(NativeDialog.SetText(target, text)
                    ? TextResult($"Set {controlRef} to \"{text}\".")
                    : ErrorResult($"The app didn't accept the text for {controlRef} (it may be hung)."));
            }

            case "close":
            {
                if (!NativeDialog.RequestClose(hwnd))
                {
                    return Task.FromResult(ErrorResult($"Could not post WM_CLOSE to {handle}."));
                }
                var closed = WaitUntil(() => !DialogMonitor.IsOpen(hwnd), TimeSpan.FromMilliseconds(750));
                return Task.FromResult(TextResult(closed
                    ? $"Closed {handle}."
                    : $"Sent close to {handle}, but it is still open (some dialogs, e.g. Yes/No message boxes, ignore it; press a button instead)."));
            }

            default:
                return Task.FromResult(ErrorResult("action must be read, press, set_text or close"));
        }
    }

    internal static string Read(string handle, nint hwnd, IReadOnlyList<NativeControl> controls)
    {
        var info = DialogMonitor.Describe(hwnd);
        var sb = new StringBuilder();
        sb.AppendLine(info != null
            ? $"{handle} {DialogClassifier.Describe(info)} (class {info.ClassName})"
            : handle);

        var shown = controls
            .Where(c => c.IsVisible && (c.Text.Length > 0 || c.Kind is NativeControlKind.Edit or NativeControlKind.ComboBox))
            .ToList();
        if (shown.Count == 0)
        {
            sb.AppendLine("No readable child controls. This is probably WPF or a task dialog; use windows_snapshot.");
        }
        foreach (var c in shown)
        {
            sb.AppendLine($"- {NativeDialog.Describe(c)}");
        }
        return sb.ToString().TrimEnd();
    }

    internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}

/// <summary>
/// Wait for a pending click to finish, or for a dialog to open or close.
/// </summary>
public class WaitTool : ToolBase
{
    private const int MaxTimeoutMs = 25_000; // stays under the 30s tool timeout

    private readonly SessionManager _sessions;
    private readonly DialogMonitor _monitor;
    private readonly PendingOperationRegistry _pending;

    public WaitTool(SessionManager sessions, DialogMonitor monitor, PendingOperationRegistry pending)
    {
        _sessions = sessions;
        _monitor = monitor;
        _pending = pending;
    }

    public override string Name => "windows_wait";

    public override string Description =>
        
        "Wait for a pending click to finish (op), a new dialog (until=dialog_open), a dialog to close (until=dialog_closed + handle), or an app to go idle (until=idle: it answers, no progress bar is running, and the UI stays unchanged for stableMs; handle, default the app's foreground window). Returns as soon as it happens.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            until = new
            {
                type = "string",
                @enum = new[] { "op_done", "dialog_open", "dialog_closed", "idle" },
                description = "What to wait for (default: op_done if op is given)"
            },
            op = new { type = "string", description = "Pending operation id, e.g. 'op2'" },
            handle = new { type = "string", description = "Dialog handle for until=dialog_closed" },
            timeoutMs = new { type = "integer", description = "Default 10000, max 25000" },
            stableMs = new { type = "integer", description = "For until=idle: how long the UI must stay unchanged (default 500)" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var opId = GetStringArgument(arguments, "op");
        var until = GetStringArgument(arguments, "until") ?? (opId != null ? "op_done" : null);
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(GetArgument<int?>(arguments, "timeoutMs") ?? 10_000, 0, MaxTimeoutMs));

        switch (until)
        {
            case "op_done":
            {
                if (string.IsNullOrEmpty(opId)) return ErrorResult("until=op_done needs 'op'");
                var op = _pending.Get(opId);
                if (op == null)
                {
                    return ErrorResult($"Unknown operation {opId}. It may have finished and been reported already.");
                }
                try
                {
                    await op.Completion.WaitAsync(timeout);
                }
                catch (TimeoutException) when (!op.IsFinished)
                {
                    // (An op can itself fail with TimeoutException, e.g. a UIA transaction timeout.)
                    return TextResult($"{op.Id} ({op.Description}) is still pending after {timeout.TotalMilliseconds:0}ms.");
                }
                catch
                {
                    // Faulted: reported below.
                }
                _pending.Forget(op);
                return TextResult($"{op.Id} ({op.Description}) finished: {op.FinishedText}");
            }

            case "dialog_open":
            {
                var pids = _sessions.TrackedProcessIds;
                if (pids.Count == 0) return ErrorResult("No apps tracked yet.");
                var baseline = _monitor.GetDialogs(pids);
                IReadOnlyList<DialogInfo> fresh = Array.Empty<DialogInfo>();
                var found = await PollAsync(() =>
                {
                    fresh = DialogClassifier.NewSince(baseline, _monitor.GetDialogs(pids));
                    return fresh.Count > 0;
                }, timeout);
                if (!found) return TextResult($"No new dialog within {timeout.TotalMilliseconds:0}ms.");
                return TextResult(string.Join("\n", fresh.Select(d =>
                    $"Dialog opened: {_sessions.RegisterNativeWindow(d.Hwnd, d.ProcessId)} {DialogClassifier.Describe(d)}")));
            }

            case "dialog_closed":
            {
                var handle = GetStringArgument(arguments, "handle");
                if (string.IsNullOrEmpty(handle)) return ErrorResult("until=dialog_closed needs 'handle'");
                var hwnd = _sessions.GetHwnd(handle);
                if (hwnd == 0) return ErrorResult($"Unknown window handle {handle}.");
                var closed = await PollAsync(() => !DialogMonitor.IsOpen(hwnd), timeout);
                return TextResult(closed
                    ? $"{handle} closed."
                    : $"{handle} is still open after {timeout.TotalMilliseconds:0}ms.");
            }

            case "idle":
            {
                var handle = GetStringArgument(arguments, "handle");
                nint hwnd;
                if (handle != null)
                {
                    hwnd = _sessions.GetHwnd(handle);
                    if (hwnd == 0) return ErrorResult($"Unknown window handle {handle}.");
                }
                else
                {
                    var foreground = NativeMethods.GetForegroundWindow();
                    var pid = foreground != 0 ? NativeMethods.GetProcessId(foreground) : 0;
                    if (pid == 0 || !_sessions.TrackedProcessIds.Contains(pid))
                    {
                        return ErrorResult("until=idle needs 'handle': no app being automated is in the foreground.");
                    }
                    hwnd = foreground;
                    handle = _sessions.RegisterNativeWindow(hwnd, pid);
                }
                var stableMs = GetArgument<int?>(arguments, "stableMs");
                var stable = stableMs is > 0 ? TimeSpan.FromMilliseconds(stableMs.Value) : IdleWaiter.DefaultStable;
                var (idle, state) = await new IdleWaiter(_sessions, _pending).WaitAsync(hwnd, timeout, stable);
                return idle
                    ? TextResult($"{handle} is idle ({state}).")
                    : TextResult($"{handle} is not idle after {timeout.TotalMilliseconds:0}ms: {state}.");
            }

            default:
                return ErrorResult("until must be op_done, dialog_open, dialog_closed or idle");
        }
    }

    /// <summary>Re-checks as soon as a new window opens anywhere (events), else every 250ms.</summary>
    private async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var watcher = UiaChangeWatcher.Start(_sessions.Automation, null);
        return await Poll.UntilAsync(condition, timeout, watcher.Signal, TimeSpan.FromMilliseconds(250));
    }
}
