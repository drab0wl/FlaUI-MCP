using System.Diagnostics;
using System.Text;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Core.Snapshots;
using PlaywrightWindows.Mcp.Core.Win32;
using PlaywrightWindows.Mcp.Tools;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Appends a bounded snapshot of the window the agent will most likely act on next (the new
/// dialog, or the app's foreground window) to action results, saving a windows_snapshot call.
/// </summary>
public sealed class PostActionSnapshotter
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);

    private readonly SessionManager _sessions;
    private readonly DialogMonitor _dialogs;
    private readonly PendingOperationRegistry _pending;
    private readonly SnapshotBuilder _builder;
    private readonly ElementRegistry _elements;

    public PostActionSnapshotter(
        SessionManager sessions,
        ElementRegistry elements,
        DialogMonitor dialogs,
        PendingOperationRegistry pending,
        PostActionOptions? options = null)
    {
        _sessions = sessions;
        _dialogs = dialogs;
        _pending = pending;
        _builder = new SnapshotBuilder(elements);
        _elements = elements;
        Options = options ?? PostActionOptions.FromEnvironment();
    }

    public PostActionOptions Options { get; }

    /// <summary>The tool's postSnapshot argument, else the server default.</summary>
    public PostActionMode ModeFor(System.Text.Json.JsonElement? arguments) =>
        PostActionModes.FromArgument(arguments, Options.Mode);

    /// <returns>The section to append, or null when there's nothing useful to show.</returns>
    public string? Capture(PostActionContext context, PostActionMode? mode = null)
    {
        var effective = mode ?? Options.Mode;
        if (effective == PostActionMode.Off) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            return CaptureCore(context, effective);
        }
        catch (Exception ex)
        {
            return $"--- after {context.Action}: could not snapshot ({ex.Message}) ---";
        }
        finally
        {
            TimingLog.Shared.Detail("post-snapshot", sw.Elapsed, $"action={context.Action}");
        }
    }

    private string? CaptureCore(PostActionContext context, PostActionMode mode)
    {
        var pid = context.ProcessId;
        var pids = pid != 0 ? new HashSet<int> { pid } : null;
        var foreground = NativeMethods.GetForegroundWindow();
        var target = PostActionTargeting.Choose(
            pid,
            context.NewDialogs,
            foreground,
            foreground != 0 ? NativeMethods.GetProcessId(foreground) : 0,
            pids != null ? _dialogs.GetDialogs(pids) : Array.Empty<DialogInfo>(),
            context.SourceWindow,
            DialogMonitor.IsOpen);
        if (target.Kind == PostActionTargetKind.None) return null;

        var hwnd = target.Hwnd;
        var targetPid = NativeMethods.GetProcessId(hwnd);
        var handle = _sessions.RegisterNativeWindow(hwnd, targetPid);
        var title = NativeMethods.GetCaption(hwnd);
        var header = PostActionTargeting.Header(context.Action, handle, title, target.Kind);

        if (_pending.HasRunningFor(targetPid) && !Responsiveness.Probe(
                () => _ = _sessions.Automation.FromHandle(hwnd).Properties.Name.ValueOrDefault, ProbeTimeout))
        {
            // A click is still inside the app's provider (behind this dialog): Win32 only, and
            // only for dialogs (a main window's child HWNDs are no summary of anything).
            var isDialog = target.Kind is PostActionTargetKind.NewDialog or PostActionTargetKind.OpenDialog
                           || NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0;
            var controls = isDialog ? NativeDialog.ReadControls(hwnd) : Array.Empty<NativeControl>();
            var body = controls.Any(c => c.IsVisible && c.Text.Length > 0)
                ? Bound(NativeDialogTool.Read(handle, hwnd, controls)) +
                  $"\n(Win32 view: UI Automation is blocked by a pending click. Use windows_dialog handle={handle} to press buttons.)"
                : $"UI Automation for {handle} is blocked by a pending click. Use windows_dialogs, windows_dialog, or windows_screenshot.";
            return $"{header}\n{body}";
        }

        var window = _sessions.GetWindow(handle);
        if (window == null) return null;

        var previous = _elements.LastSnapshot(handle);
        var result = _builder.Build(handle, window, new SnapshotLimits
        {
            MaxNodes = Math.Max(Options.WalkMaxNodes, Options.MaxNodes),
            DeadlineUtc = DateTime.UtcNow + Options.TimeBudget,
        });
        // A new dialog's first snapshot stays small in Changes mode: you act on it next.
        var (maxNodes, maxChars) = mode == PostActionMode.Changes && target.Kind == PostActionTargetKind.NewDialog
            ? (Options.DialogMaxNodes, Options.DialogMaxChars)
            : (Options.MaxNodes, Options.MaxChars);
        var full = SnapshotText.CompactText(result.Text, maxNodes, maxChars).TrimEnd();

        var comparable = previous is { Complete: true } && !result.Truncated;
        SnapshotDiff? diff = null;
        var changes = "";
        if (comparable)
        {
            diff = SnapshotDiff.Compute(ParsedSnapshot.Parse(previous!.Text), ParsedSnapshot.Parse(result.Text));
            changes = diff.Render(mode == PostActionMode.Changes ? Options.ChangesMaxChars : Options.MaxChars).TrimEnd();
        }

        switch (PostActionModes.Choose(mode, target.Kind, comparable, changes.Length, full.Length))
        {
            case PostActionView.Changes:
                var detail = diff!.IsEmpty ? $"unchanged, {diff.Unchanged} elements" : $"changes: {diff.Summary}";
                var diffHeader = PostActionTargeting.Header(context.Action, handle, title, target.Kind, detail);
                return changes.Length == 0 ? diffHeader : $"{diffHeader}\n{changes}";

            case PostActionView.Summary:
                // Nothing to compare with yet. This read is now the baseline, so the next action
                // in this window reports changes.
                var summary = result.Truncated
                    ? $"{result.Nodes}+ elements, too many to compare in time; use windows_find to look for what you need"
                    : $"first look, {result.Nodes} elements; use windows_find or windows_snapshot compact=true to read it";
                return PostActionTargeting.Header(context.Action, handle, title, target.Kind, summary);

            default:
                return $"{header}\n{full}";
        }
    }

    private string Bound(string text) =>
        text.Length <= Options.MaxChars ? text : text[..Options.MaxChars] + "\n... (truncated)";

    /// <summary>Capture after acting on an element known by ref.</summary>
    public string? CaptureAfter(string action, string refId, FlaUI.Core.AutomationElements.AutomationElement element, PostActionMode? mode = null)
    {
        var pid = 0;
        try { pid = element.Properties.ProcessId.ValueOrDefault; } catch { }
        var hwnd = ElementRegistry.WindowHandleOf(refId) is { } handle ? _sessions.GetHwnd(handle) : 0;
        if (pid == 0 && hwnd != 0) pid = NativeMethods.GetProcessId(hwnd);
        return Capture(new PostActionContext(action, pid, hwnd, Array.Empty<DialogInfo>()), mode);
    }

    /// <summary>Appends the post-action section to a result text.</summary>
    public static string Append(string text, string? section) =>
        string.IsNullOrEmpty(section) ? text : $"{text.TrimEnd()}\n\n{section}";
}
