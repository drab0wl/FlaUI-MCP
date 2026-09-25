using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Batch;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Core.Snapshots;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Execute multiple actions in a single call for better performance
/// </summary>
public class BatchTool : ToolBase
{
    // Stays under the 30s tool timeout, leaving room for the post-action snapshot.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(24);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly ClickExecutor _clickExecutor;
    private readonly DialogMonitor _dialogs;
    private readonly PendingOperationRegistry _pending;
    private readonly ElementFinder _finder;
    private readonly PostActionSnapshotter? _post;

    public BatchTool(SessionManager sessionManager, ElementRegistry elementRegistry, ClickExecutor clickExecutor)
        : this(sessionManager, elementRegistry, clickExecutor, new DialogMonitor(), new PendingOperationRegistry())
    {
    }

    public BatchTool(
        SessionManager sessionManager,
        ElementRegistry elementRegistry,
        ClickExecutor clickExecutor,
        DialogMonitor dialogs,
        PendingOperationRegistry pending,
        PostActionSnapshotter? post = null)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _snapshotBuilder = new SnapshotBuilder(elementRegistry);
        _clickExecutor = clickExecutor;
        _dialogs = dialogs;
        _pending = pending;
        _finder = new ElementFinder(sessionManager, elementRegistry, dialogs, pending);
        _post = post;
    }

    public override string Name => "windows_batch";

    public override string Description =>
        "Execute multiple actions in a single call. Much faster than individual calls. " +
        "Actions: click, type, fill, keys (chords like Ctrl+Shift+B, to the focused element or a ref/selector), " +
        "wait, snapshot, dialog_press, dialog_set_text. " +
        "Target elements by ref, or by selector (name / nameContains / automationId / role, optionally " +
        "handle to limit the search to one window; handle \"$dialog\" is the dialog the batch last saw open), " +
        "so a whole flow can run without a snapshot in between. " +
        "wait takes ms, or until=dialog_open | dialog_closed | element | element_gone | text_contains (+ timeoutMs). " +
        "A click that opens a dialog stops the batch, unless the next action is wait until=dialog_open: then the " +
        "dialog is expected and the batch goes on (dialog_press/dialog_set_text default to that dialog). " +
        "noDialog=true on a click skips the 250ms dialog-settle wait. " +
        "The result ends with a bounded snapshot of the window shown after the last action (postSnapshot=false to skip).";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            actions = new
            {
                type = "array",
                description = "List of actions to execute in order",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        action = new
                        {
                            type = "string",
                            @enum = new[] { "click", "type", "fill", "keys", "wait", "snapshot", "dialog_press", "dialog_set_text" },
                            description = "Action type"
                        },
                        @ref = new
                        {
                            type = "string",
                            description = "Element ref for click/type/fill actions"
                        },
                        selector = new
                        {
                            type = "object",
                            description = "Find the element instead of using a ref (also accepted as fields on the action itself). " +
                                          "Used by click/type/fill and wait until=element|element_gone|text_contains.",
                            properties = new
                            {
                                name = new { type = "string", description = "Exact name, as shown in snapshots" },
                                nameContains = new { type = "string", description = "Substring of the name" },
                                automationId = new { type = "string" },
                                role = new { type = "string", description = "Role as shown in snapshots: button, textbox, checkbox, menuitem, tab, ..." },
                                handle = new { type = "string", description = "Window to search (\"w3\", or \"$dialog\"). Default: the app's foreground window, then its dialogs and windows" },
                                index = new { type = "integer", description = "0-based, when several elements match" }
                            }
                        },
                        text = new
                        {
                            type = "string",
                            description = "Text for type and dialog_set_text; expected text for wait until=text_contains"
                        },
                        mode = new
                        {
                            type = "string",
                            @enum = new[] { "auto", "invoke", "input" },
                            description = "Click mode for click actions (see windows_click)"
                        },
                        value = new
                        {
                            type = "string",
                            description = "Value for fill action"
                        },
                        keys = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "For keys: chords to press in order, e.g. [\"Ctrl+Shift+B\"] or [\"Down\", \"Enter\"]"
                        },
                        ms = new
                        {
                            type = "integer",
                            description = "Milliseconds for a plain wait (default: 100)"
                        },
                        until = new
                        {
                            type = "string",
                            @enum = WaitConditions.All,
                            description = "Wait for a condition instead of a fixed time"
                        },
                        timeoutMs = new
                        {
                            type = "integer",
                            description = "Timeout for wait until=... (default: 5000)"
                        },
                        compact = new
                        {
                            type = "boolean",
                            description = "For snapshot: hide offscreen elements and layout-only groups"
                        },
                        handle = new
                        {
                            type = "string",
                            description = "Window handle for snapshot; dialog handle for dialog_press/dialog_set_text/wait until=dialog_closed (default: the last dialog)"
                        },
                        button = new
                        {
                            type = "string",
                            description = "For dialog_press: button text, control ref, or standard name (ok, cancel, yes, no, ...)"
                        },
                        control = new
                        {
                            type = "string",
                            description = "For dialog_set_text: control ref from windows_dialog read (default: the first edit box)"
                        },
                        noDialog = new
                        {
                            type = "boolean",
                            description = "For click: this click doesn't open a dialog, so don't wait to see one appear"
                        },
                        settleMs = new
                        {
                            type = "integer",
                            description = "For click: how long to watch for a dialog after the click returns (default: 250)"
                        }
                    },
                    required = new[] { "action" }
                }
            },
            stopOnError = new
            {
                type = "boolean",
                description = "Stop executing if an action fails (default: true)"
            },
            postSnapshot = new
            {
                type = "boolean",
                description = "Append a snapshot of the window shown after the last action (default: true)"
            }
        },
        required = new[] { "actions" }
    };

    /// <summary>State carried from one step to the next.</summary>
    private sealed class Run
    {
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public TimeSpan Remaining => Budget - Clock.Elapsed;

        /// <summary>Dialogs open before the latest action, to tell which ones it opened.</summary>
        public IReadOnlyList<DialogInfo> Baseline { get; set; } = Array.Empty<DialogInfo>();

        /// <summary>The newest dialog seen opening in this batch ("$dialog").</summary>
        public string? LastDialog { get; set; }

        /// <summary>For the post-action snapshot.</summary>
        public PostActionContext? LastAction { get; set; }
    }

    private sealed record StepResult(string Text, bool IsError, bool Stop = false, string? StopNote = null);

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        if (arguments == null || !arguments.Value.TryGetProperty("actions", out var actionsElement)
            || actionsElement.ValueKind != JsonValueKind.Array)
        {
            return ErrorResult("Missing required argument: actions");
        }

        var stopOnError = true;
        if (arguments.Value.TryGetProperty("stopOnError", out var stopProp))
        {
            stopOnError = stopProp.GetBoolean();
        }

        var steps = actionsElement.EnumerateArray().Select(BatchStep.Parse).ToList();
        var results = new List<string>();
        var run = new Run();
        var lastWasSnapshot = false;

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (run.Remaining <= TimeSpan.Zero)
            {
                results.Add($"Stopped before action {index + 1}: the batch used its {Budget.TotalSeconds:0}s time budget. " +
                            "Continue with the remaining actions in another call.");
                break;
            }

            var sw = Stopwatch.StartNew();
            StepResult result;
            try
            {
                result = await ExecuteStepAsync(steps, index, run);
            }
            catch (Exception ex)
            {
                result = new StepResult($"ERROR: {ex.Message}", true);
            }
            TimingLog.Shared.Detail($"batch step {index + 1} {step.Action}", sw.Elapsed);

            lastWasSnapshot = step.Action == "snapshot" && !result.IsError;
            results.Add(result.Text.StartsWith("ERROR:", StringComparison.Ordinal)
                ? $"{index + 1}. {result.Text}"
                : $"{index + 1}. {step.Action}: {result.Text}");

            if (result.Stop)
            {
                if (index + 1 < steps.Count && result.StopNote != null) results.Add(result.StopNote);
                break;
            }
            if (result.IsError && stopOnError)
            {
                results.Add($"Stopped at action {index + 1} due to error");
                break;
            }
        }

        var text = string.Join("\n", results);
        if (_post != null && !lastWasSnapshot && run.LastAction != null
            && _post.IsEnabled(GetArgument<bool?>(arguments, "postSnapshot")))
        {
            text = PostActionSnapshotter.Append(text, _post.Capture(run.LastAction));
        }
        return TextResult(text);
    }

    private async Task<StepResult> ExecuteStepAsync(IReadOnlyList<BatchStep> steps, int index, Run run)
    {
        var step = steps[index];
        if (step.SelectorError != null) return new StepResult(step.SelectorError, true);

        switch (step.Action)
        {
            case "click":
                return await ExecuteClickAsync(steps, index, run);
            case "type":
                return Act(run, step, ExecuteType);
            case "fill":
                return Act(run, step, ExecuteFill);
            case "keys":
            case "send_keys":
                return await ExecuteKeysAsync(steps, index, run);
            case "wait":
                return await ExecuteWaitAsync(step, run);
            case "snapshot":
                return ExecuteSnapshot(step);
            case "dialog_press":
                return ExecuteDialogPress(step, run);
            case "dialog_set_text":
                return ExecuteDialogSetText(step, run);
            default:
                return new StepResult($"Unknown action: {step.Action}", false);
        }
    }

    // ---------- element actions ----------

    /// <summary>The step's element, from its ref or selector.</summary>
    private (AutomationElement? Element, string? Ref, string? Error) Resolve(BatchStep step, Run run)
    {
        if (!string.IsNullOrEmpty(step.Ref))
        {
            var element = _elementRegistry.GetElement(step.Ref);
            return element == null ? (null, null, $"Element not found: {step.Ref}") : (element, step.Ref, null);
        }
        if (step.Selector != null)
        {
            var found = _finder.Find(step.Selector, run.LastDialog, out var error);
            return found == null ? (null, null, error) : (found.Element, found.Ref, null);
        }
        return (null, null, null);
    }

    private async Task<StepResult> ExecuteClickAsync(IReadOnlyList<BatchStep> steps, int index, Run run)
    {
        var step = steps[index];
        var (element, refId, error) = Resolve(step, run);
        if (error != null) return new StepResult(error, true);
        if (element == null || refId == null) return new StepResult("Missing ref or selector", true);

        if (!ClickTool.TryParseMode(step.Mode, out var mode))
        {
            return new StepResult("mode must be one of: auto, invoke, input", true);
        }

        var options = new ActionRunOptions();
        if (BatchPlan.SettleTime(steps, index) is { } settle) options = options with { SettleTime = settle };

        run.Baseline = _dialogs.GetDialogs(null);
        var click = await _clickExecutor.ClickAsync(element, new ClickRequest(refId, mode, Options: options));
        run.LastAction = new PostActionContext("batch", click.ProcessId, click.WindowHwnd, click.NewDialogs);
        RememberDialogs(run, click.NewDialogs);

        var text = step.Ref == null ? $"{step.Selector!.Describe()} -> {refId}: {click.Text}" : click.Text;
        if (BatchPlan.StopAfterClick(click.DialogOpened, click.StillRunning, steps, index))
        {
            return new StepResult(text, click.IsError, Stop: true,
                StopNote: $"Stopped after action {index + 1}: a dialog opened or the click is still pending. " +
                          "Handle it, then continue with the remaining actions. (If the dialog was expected, " +
                          "put a wait with until=dialog_open right after the click.)");
        }
        return new StepResult(text, click.IsError);
    }

    private StepResult Act(Run run, BatchStep step, Func<BatchStep, AutomationElement?, string> action)
    {
        var (element, refId, error) = Resolve(step, run);
        // A stale ref was never an error for type/fill (only a selector miss is).
        if (error != null) return new StepResult(error, step.Ref == null);

        run.Baseline = _dialogs.GetDialogs(null);
        var text = action(step, element);
        if (refId != null)
        {
            var hwnd = ElementRegistry.WindowHandleOf(refId) is { } h ? _sessionManager.GetHwnd(h) : 0;
            var pid = hwnd != 0 ? NativeMethods.GetProcessId(hwnd) : 0;
            run.LastAction = new PostActionContext("batch", pid, hwnd, Array.Empty<DialogInfo>());
        }
        if (step.Ref == null && step.Selector != null && refId != null)
        {
            text = $"{step.Selector.Describe()} -> {refId}: {text}";
        }
        return new StepResult(text, false);
    }

    private static string ExecuteType(BatchStep step, AutomationElement? element)
    {
        var text = step.Text;
        if (string.IsNullOrEmpty(text))
        {
            return "Missing text";
        }

        if (element != null)
        {
            element.Focus();
            Thread.Sleep(30);
        }

        Keyboard.Type(text);
        return $"Typed \"{text}\"";
    }

    private async Task<StepResult> ExecuteKeysAsync(IReadOnlyList<BatchStep> steps, int index, Run run)
    {
        var step = steps[index];
        if (step.Keys is not { Count: > 0 }) return new StepResult("keys needs 'keys' (e.g. [\"Ctrl+Shift+B\"]) or 'chord'", true);

        var (element, refId, error) = Resolve(step, run);
        if (error != null) return new StepResult(error, true);
        if (element != null)
        {
            element.Focus();
            Thread.Sleep(30);
        }

        run.Baseline = _dialogs.GetDialogs(null);
        if (!SendKeysTool.TrySendSequence(step.Keys, out var sent, out var keyError))
        {
            return new StepResult(keyError!, true);
        }

        // Keys go to whatever has focus: report on the foreground window afterwards.
        var foreground = NativeMethods.GetForegroundWindow();
        var hwnd = refId != null && ElementRegistry.WindowHandleOf(refId) is { } h ? _sessionManager.GetHwnd(h) : foreground;
        run.LastAction = new PostActionContext("batch", hwnd != 0 ? NativeMethods.GetProcessId(hwnd) : 0, hwnd, Array.Empty<DialogInfo>());
        var text = $"Sent {sent} to {refId ?? "the focused element"}";

        // Like a click: a dialog the keys opened stops the batch unless the next step expects it.
        var settle = BatchPlan.SettleTime(steps, index) ?? new ActionRunOptions().SettleTime;
        if (settle > TimeSpan.Zero && !BatchPlan.ExpectsDialog(steps, index))
        {
            var pids = _sessionManager.TrackedProcessIds;
            IReadOnlyList<DialogInfo> opened = Array.Empty<DialogInfo>();
            await PollAsync(() =>
            {
                opened = DialogClassifier.NewSince(run.Baseline, _dialogs.GetDialogs(pids)).Where(d => d.IsBlocking).ToList();
                return opened.Count > 0;
            }, settle);
            if (opened.Count > 0)
            {
                RememberDialogs(run, opened);
                run.LastAction = run.LastAction with { NewDialogs = opened };
                var dialogs = string.Join("; ", opened.Select(d =>
                    $"{_sessionManager.RegisterNativeWindow(d.Hwnd, d.ProcessId)} {DialogClassifier.Describe(d)}"));
                return new StepResult($"{text}. They opened: {dialogs}", false, Stop: true,
                    StopNote: $"Stopped after action {index + 1}: the keys opened a dialog. Handle it, then continue " +
                              "with the remaining actions. (If the dialog was expected, put a wait with until=dialog_open right after.)");
            }
        }
        return new StepResult(text, false);
    }

    private static string ExecuteFill(BatchStep step, AutomationElement? element)
    {
        var value = step.Value;
        if (element == null || value == null)
        {
            return "Missing ref or value";
        }

        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(value);
            return $"Filled with \"{value}\"";
        }

        // Fallback
        element.Focus();
        Thread.Sleep(30);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(30);
        Keyboard.Type(value);
        return $"Filled with \"{value}\"";
    }

    // ---------- waits ----------

    private async Task<StepResult> ExecuteWaitAsync(BatchStep step, Run run)
    {
        if (step.Until == null)
        {
            var ms = step.Ms ?? 100;
            await Task.Delay(ms);
            return new StepResult($"Waited {ms}ms", false);
        }

        var timeout = BatchPlan.WaitTimeout(step, run.Remaining);
        switch (step.Until)
        {
            case WaitConditions.DialogOpen:
            {
                var pids = _sessionManager.TrackedProcessIds;
                IReadOnlyList<DialogInfo> fresh = Array.Empty<DialogInfo>();
                var found = await PollAsync(() =>
                {
                    fresh = DialogClassifier.NewSince(run.Baseline, _dialogs.GetDialogs(pids))
                        .Where(d => DialogMonitor.IsOpen(d.Hwnd))
                        .ToList();
                    return fresh.Count > 0;
                }, timeout);
                if (!found) return new StepResult($"No new dialog within {timeout.TotalMilliseconds:0}ms", true);

                RememberDialogs(run, fresh);
                if (run.LastAction != null)
                {
                    run.LastAction = run.LastAction with { NewDialogs = fresh };
                }
                return new StepResult(string.Join("; ", fresh.Select(d =>
                    $"dialog opened: {_sessionManager.RegisterNativeWindow(d.Hwnd, d.ProcessId)} {DialogClassifier.Describe(d)}")), false);
            }

            case WaitConditions.DialogClosed:
            {
                var handle = DialogHandle(step, run);
                if (handle == null) return new StepResult("wait until=dialog_closed needs 'handle' (no dialog seen in this batch)", true);
                var hwnd = _sessionManager.GetHwnd(handle);
                if (hwnd == 0) return new StepResult($"Unknown window handle {handle}", true);
                var closed = await PollAsync(() => !DialogMonitor.IsOpen(hwnd), timeout);
                if (closed && run.LastAction != null)
                {
                    run.LastAction = run.LastAction with { NewDialogs = Array.Empty<DialogInfo>() };
                }
                return closed
                    ? new StepResult($"{handle} closed", false)
                    : new StepResult($"{handle} is still open after {timeout.TotalMilliseconds:0}ms", true);
            }

            case WaitConditions.Element:
            case WaitConditions.ElementGone:
            case WaitConditions.TextContains:
                return await WaitForElementAsync(step, run, timeout);

            default:
                return new StepResult($"until must be one of: {string.Join(", ", WaitConditions.All)}", true);
        }
    }

    private async Task<StepResult> WaitForElementAsync(BatchStep step, Run run, TimeSpan timeout)
    {
        if (step.Selector == null && string.IsNullOrEmpty(step.Ref))
        {
            return new StepResult($"wait until={step.Until} needs a selector (or ref)", true);
        }
        if (step.Until == WaitConditions.TextContains && string.IsNullOrEmpty(step.Text))
        {
            return new StepResult("wait until=text_contains needs 'text'", true);
        }

        string? lastText = null;
        string? lastRef = null;
        string? lastError = null;
        var met = await PollAsync(() =>
        {
            var (element, refId, error) = Resolve(step, run);
            lastError = error;
            lastRef = refId;
            switch (step.Until)
            {
                case WaitConditions.Element:
                    return element != null;
                case WaitConditions.ElementGone:
                    return element == null || IsGone(element);
                default:
                    if (element == null) return false;
                    try { lastText = ElementText.Read(element); } catch { lastText = null; }
                    return BatchPlan.TextMatches(lastText, step.Text!);
            }
        }, timeout);

        var what = step.Selector?.Describe() ?? step.Ref;
        if (met)
        {
            return step.Until switch
            {
                WaitConditions.Element => new StepResult($"{what} appeared ({lastRef})", false),
                WaitConditions.ElementGone => new StepResult($"{what} is gone", false),
                _ => new StepResult($"{what} ({lastRef}) text contains \"{step.Text}\"", false),
            };
        }
        return step.Until switch
        {
            WaitConditions.Element => new StepResult($"{what} did not appear within {timeout.TotalMilliseconds:0}ms" +
                                                      (lastError != null ? $": {lastError}" : ""), true),
            WaitConditions.ElementGone => new StepResult($"{what} is still there after {timeout.TotalMilliseconds:0}ms", true),
            _ => new StepResult($"{what} text did not contain \"{step.Text}\" within {timeout.TotalMilliseconds:0}ms " +
                                $"(last: {(lastText == null ? lastError ?? "no element" : $"\"{Truncate(lastText, 200)}\"")})", true),
        };
    }

    /// <summary>A ref whose element has been removed from the UI.</summary>
    private static bool IsGone(AutomationElement element)
    {
        try
        {
            _ = element.Properties.ProcessId.Value;
            return element.Properties.IsOffscreen.ValueOrDefault && element.BoundingRectangle.IsEmpty;
        }
        catch
        {
            return true;
        }
    }

    private static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return true;
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(PollInterval);
        }
    }

    // ---------- dialogs (Win32, works while UI Automation is blocked) ----------

    private StepResult ExecuteDialogPress(BatchStep step, Run run)
    {
        if (string.IsNullOrWhiteSpace(step.Button)) return new StepResult("dialog_press needs 'button'", true);
        var (handle, hwnd, error) = OpenDialog(step, run);
        if (error != null) return new StepResult(error, true);

        var pid = NativeMethods.GetProcessId(hwnd);
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        var controls = NativeDialog.ReadControls(hwnd);
        var target = NativeDialog.FindButton(controls, step.Button);
        if (target == null)
        {
            return new StepResult($"No button matching \"{step.Button}\" in {handle}.\n{NativeDialogTool.Read(handle!, hwnd, controls)}", true);
        }
        if (!target.IsEnabled) return new StepResult($"{target.Ref} \"{target.Text}\" is disabled.", true);

        run.Baseline = _dialogs.GetDialogs(null);
        if (!NativeDialog.Press(target)) return new StepResult($"Could not post a click to {target.Ref}.", true);

        var closed = NativeDialogTool.WaitUntil(() => !DialogMonitor.IsOpen(hwnd), TimeSpan.FromMilliseconds(750));
        var opened = DialogClassifier.NewSince(run.Baseline, _dialogs.GetDialogs(new HashSet<int> { pid }));
        run.LastAction = new PostActionContext("batch", pid, closed ? owner : hwnd, opened);
        RememberDialogs(run, opened);
        return new StepResult(
            $"Pressed {target.Ref} \"{target.Text}\" in {handle}. " + (closed ? "The dialog closed." : "The dialog is still open."),
            false);
    }

    private StepResult ExecuteDialogSetText(BatchStep step, Run run)
    {
        if (step.Text == null) return new StepResult("dialog_set_text needs 'text'", true);
        var (handle, hwnd, error) = OpenDialog(step, run);
        if (error != null) return new StepResult(error, true);

        var controls = NativeDialog.ReadControls(hwnd);
        var target = step.Control != null
            ? controls.FirstOrDefault(c => string.Equals(c.Ref, step.Control, StringComparison.OrdinalIgnoreCase))
            : controls.FirstOrDefault(c => c.IsVisible && c.IsEnabled && c.Kind is NativeControlKind.Edit or NativeControlKind.ComboBox);
        if (target == null)
        {
            return new StepResult(
                (step.Control != null ? $"No control {step.Control} in {handle}." : $"No edit box in {handle}.") +
                $"\n{NativeDialogTool.Read(handle!, hwnd, controls)}", true);
        }
        if (target.Kind is not (NativeControlKind.Edit or NativeControlKind.ComboBox))
        {
            return new StepResult($"{target.Ref} is a {target.Kind}, not an edit box.", true);
        }

        run.LastAction = new PostActionContext("batch", NativeMethods.GetProcessId(hwnd), hwnd, Array.Empty<DialogInfo>());
        return NativeDialog.SetText(target, step.Text)
            ? new StepResult($"Set {target.Ref} in {handle} to \"{step.Text}\".", false)
            : new StepResult($"The app didn't accept the text for {target.Ref} (it may be hung).", true);
    }

    /// <summary>The dialog a dialog_* step acts on: its handle, else the batch's last dialog, else the one open dialog.</summary>
    private (string? Handle, nint Hwnd, string? Error) OpenDialog(BatchStep step, Run run)
    {
        var handle = DialogHandle(step, run);
        if (handle == null)
        {
            var open = _dialogs.GetDialogs(_sessionManager.TrackedProcessIds).Where(d => d.IsBlocking && d.IsEnabled).ToList();
            if (open.Count == 0) return (null, 0, "No dialog is open. Pass 'handle'.");
            handle = _sessionManager.RegisterNativeWindow(open[0].Hwnd, open[0].ProcessId);
        }

        var hwnd = _sessionManager.GetHwnd(handle);
        if (hwnd == 0) return (handle, 0, $"Unknown window handle {handle}.");
        if (!DialogMonitor.IsOpen(hwnd)) return (handle, hwnd, $"{handle} is no longer open.");
        return (handle, hwnd, null);
    }

    private static string? DialogHandle(BatchStep step, Run run)
    {
        var handle = step.Handle ?? step.Selector?.Handle;
        return handle == null || handle == ElementSelector.LastDialog ? run.LastDialog : handle;
    }

    private void RememberDialogs(Run run, IReadOnlyList<DialogInfo> dialogs)
    {
        var newest = dialogs.LastOrDefault(d => d.IsEnabled) ?? dialogs.LastOrDefault();
        if (newest != null)
        {
            run.LastDialog = _sessionManager.RegisterNativeWindow(newest.Hwnd, newest.ProcessId);
        }
    }

    // ---------- snapshot ----------

    private StepResult ExecuteSnapshot(BatchStep step)
    {
        var handle = step.Handle;

        Window? window = null;
        if (!string.IsNullOrEmpty(handle))
        {
            window = _sessionManager.GetWindow(handle);
            if (window == null)
            {
                return new StepResult($"Window not found: {handle}", false);
            }
        }
        else
        {
            // Get focused window
            var focusedElement = _sessionManager.Automation.FocusedElement();
            if (focusedElement != null)
            {
                var current = focusedElement;
                while (current != null)
                {
                    if (current.Properties.ControlType.ValueOrDefault == FlaUI.Core.Definitions.ControlType.Window)
                    {
                        window = current.AsWindow();
                        handle = _sessionManager.RegisterWindow(window);
                        break;
                    }
                    current = current.Parent;
                }
            }
        }

        if (window == null)
        {
            return new StepResult("No window found", false);
        }

        var snapshot = _snapshotBuilder.BuildSnapshot(handle!, window);
        if (step.Compact ?? SnapshotTool.CompactByDefault) snapshot = SnapshotText.CompactText(snapshot);
        return new StepResult($"\n{snapshot}", false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
