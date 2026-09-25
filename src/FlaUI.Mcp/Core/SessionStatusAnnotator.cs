using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Appends open dialogs and pending/finished clicks to every tool result, so the agent
/// learns about a dialog on its very next call, whatever tool that is.
/// </summary>
public sealed class SessionStatusAnnotator : IToolResultAnnotator
{
    private readonly SessionManager _sessions;
    private readonly DialogMonitor _monitor;
    private readonly PendingOperationRegistry _pending;

    public SessionStatusAnnotator(SessionManager sessions, DialogMonitor monitor, PendingOperationRegistry pending)
    {
        _sessions = sessions;
        _monitor = monitor;
        _pending = pending;
    }

    public string? GetFooter(string toolName)
    {
        // windows_dialogs already lists dialogs and pending actions in its body.
        var listsItself = toolName == "windows_dialogs";

        var pids = _sessions.TrackedProcessIds;
        var dialogs = listsItself || pids.Count == 0
            ? Array.Empty<DialogInfo>()
            : _monitor.GetDialogs(pids);
        var running = listsItself ? Array.Empty<PendingOperation>() : _pending.Running();
        var finished = _pending.TakeFinished();

        return StatusFooter.Build(
            dialogs,
            d => _sessions.RegisterNativeWindow(d.Hwnd, d.ProcessId),
            running,
            finished);
    }
}
