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
        Options = options ?? PostActionOptions.FromEnvironment();
    }

    public PostActionOptions Options { get; }

    /// <summary>The tool argument wins; otherwise the server default.</summary>
    public bool IsEnabled(bool? requested) => requested ?? Options.Enabled;

    /// <returns>The section to append, or null when there's nothing useful to show.</returns>
    public string? Capture(PostActionContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return CaptureCore(context);
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

    private string? CaptureCore(PostActionContext context)
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

        var result = _builder.Build(handle, window, new SnapshotLimits
        {
            MaxNodes = Options.MaxNodes,
            MaxChars = Options.MaxChars,
            DeadlineUtc = DateTime.UtcNow + Options.TimeBudget,
        });
        return $"{header}\n{result.Text.TrimEnd()}";
    }

    private string Bound(string text) =>
        text.Length <= Options.MaxChars ? text : text[..Options.MaxChars] + "\n... (truncated)";

    /// <summary>Capture after acting on an element known by ref.</summary>
    public string? CaptureAfter(string action, string refId, FlaUI.Core.AutomationElements.AutomationElement element)
    {
        var pid = 0;
        try { pid = element.Properties.ProcessId.ValueOrDefault; } catch { }
        var hwnd = ElementRegistry.WindowHandleOf(refId) is { } handle ? _sessions.GetHwnd(handle) : 0;
        if (pid == 0 && hwnd != 0) pid = NativeMethods.GetProcessId(hwnd);
        return Capture(new PostActionContext(action, pid, hwnd, Array.Empty<DialogInfo>()));
    }

    /// <summary>Appends the post-action section to a result text.</summary>
    public static string Append(string text, string? section) =>
        string.IsNullOrEmpty(section) ? text : $"{text.TrimEnd()}\n\n{section}";
}
