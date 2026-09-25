using FlaUI.Core.AutomationElements;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Keyboard input goes to whatever window has focus. Before typing or sending keys, check it's
/// the app being automated, so a focus change (a notification, the user clicking elsewhere)
/// can't send the agent's keystrokes into another program. FLAUI_MCP_KEYBOARD_GUARD=0 turns
/// it off.
/// </summary>
public sealed class KeyboardGuard
{
    private readonly SessionManager _sessions;

    public KeyboardGuard(SessionManager sessions, bool? enabled = null)
    {
        _sessions = sessions;
        Enabled = enabled ?? Environment.GetEnvironmentVariable("FLAUI_MCP_KEYBOARD_GUARD")?.Trim() is not ("0" or "false" or "off");
    }

    public bool Enabled { get; }

    /// <summary>
    /// Null if keys may be sent. With a target element (already focused), its app must be in the
    /// foreground; the window is brought forward once if it isn't. Without one, the foreground
    /// app must be one being automated.
    /// </summary>
    public string? Check(AutomationElement? target)
    {
        if (!Enabled) return null;

        int? targetPid = null;
        if (target != null)
        {
            try { targetPid = target.Properties.ProcessId.ValueOrDefault; } catch { }
        }

        var decision = KeyboardPolicy.Decide(ForegroundPid(), _sessions.TrackedProcessIds, targetPid);
        if (decision == null) return null;

        if (targetPid is > 0)
        {
            // Focus() doesn't always activate the window: try once.
            nint hwnd = 0;
            try { hwnd = NativeMethods.RootOf(target!.Properties.NativeWindowHandle.ValueOrDefault); } catch { }
            if (hwnd == 0) hwnd = TopLevelOf(targetPid.Value);
            if (hwnd != 0 && NativeMethods.BringToForeground(hwnd))
            {
                decision = KeyboardPolicy.Decide(ForegroundPid(), _sessions.TrackedProcessIds, targetPid);
                if (decision == null) return null;
            }
        }

        var foreground = NativeMethods.GetForegroundWindow();
        return KeyboardPolicy.Message(decision.Value, NativeMethods.GetCaption(foreground), ForegroundPid());
    }

    private static int ForegroundPid()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == 0 ? 0 : NativeMethods.GetProcessId(foreground);
    }

    private nint TopLevelOf(int pid)
    {
        nint found = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.GetProcessId(hwnd) == pid && NativeMethods.IsWindowVisible(hwnd)
                && NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == 0)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, 0);
        return found;
    }
}
