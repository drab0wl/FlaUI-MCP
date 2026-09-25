namespace PlaywrightWindows.Mcp.Core;

/// <summary>Whether keys may be sent, given what's in the foreground. Pure.</summary>
public static class KeyboardPolicy
{
    public enum Refusal { NotTarget, NotTracked }

    /// <summary>Pure decision: null to allow.</summary>
    public static Refusal? Decide(int foregroundPid, IReadOnlySet<int> tracked, int? targetPid)
    {
        if (targetPid is > 0) return foregroundPid == targetPid ? null : Refusal.NotTarget;
        if (tracked.Count == 0) return null; // nothing known yet: can't judge
        return tracked.Contains(foregroundPid) ? null : Refusal.NotTracked;
    }

    public static string Message(Refusal refusal, string foregroundTitle, int foregroundPid) =>
        $"Not sending keys: the foreground window is \"{foregroundTitle}\" (pid {foregroundPid}), " +
        (refusal == Refusal.NotTarget
            ? "not the target element's app, and it couldn't be brought forward. "
            : "which isn't an app being automated. ") +
        "Bring the app forward with windows_focus, or pass a ref so the element is focused first. " +
        "(FLAUI_MCP_KEYBOARD_GUARD=0 turns this check off.)";
}
