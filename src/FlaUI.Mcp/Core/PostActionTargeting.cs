using PlaywrightWindows.Mcp.Core.Dialogs;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Settings for the snapshot appended to action results.
/// Environment: FLAUI_MCP_POST_SNAPSHOT=0 turns it off by default (tools can still ask for it),
/// FLAUI_MCP_POST_SNAPSHOT_MAX_NODES / _MAX_CHARS bound its size.
/// </summary>
public sealed record PostActionOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxNodes { get; init; } = 150;
    public int MaxChars { get; init; } = 8000;
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(4);

    public static PostActionOptions FromEnvironment()
    {
        var options = new PostActionOptions();
        var enabled = Environment.GetEnvironmentVariable("FLAUI_MCP_POST_SNAPSHOT");
        if (enabled != null && enabled.Trim().ToLowerInvariant() is "0" or "false" or "off" or "no")
        {
            options = options with { Enabled = false };
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("FLAUI_MCP_POST_SNAPSHOT_MAX_NODES"), out var nodes) && nodes > 0)
        {
            options = options with { MaxNodes = nodes };
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("FLAUI_MCP_POST_SNAPSHOT_MAX_CHARS"), out var chars) && chars > 0)
        {
            options = options with { MaxChars = chars };
        }
        return options;
    }
}

/// <summary>What happened, as far as choosing a window to show afterwards goes.</summary>
/// <param name="ProcessId">The app the action went to.</param>
/// <param name="SourceWindow">Top-level window the action targeted (0 if unknown).</param>
/// <param name="NewDialogs">Dialogs the action opened.</param>
public sealed record PostActionContext(string Action, int ProcessId, nint SourceWindow, IReadOnlyList<DialogInfo> NewDialogs);

public enum PostActionTargetKind
{
    None,
    NewDialog,
    Foreground,
    OpenDialog,
    SourceWindow,
}

public readonly record struct PostActionTarget(nint Hwnd, PostActionTargetKind Kind);

/// <summary>Which window to show after an action. Pure, so it's unit tested.</summary>
public static class PostActionTargeting
{
    public static PostActionTarget Choose(
        int processId,
        IReadOnlyList<DialogInfo> newDialogs,
        nint foreground,
        int foregroundProcessId,
        IReadOnlyList<DialogInfo> openDialogs,
        nint sourceWindow,
        Func<nint, bool> isOpen)
    {
        // A dialog the action opened; with nested dialogs, the enabled (innermost) one.
        var opened = newDialogs.Where(d => isOpen(d.Hwnd)).ToList();
        if (opened.Count > 0)
        {
            var dialog = opened.LastOrDefault(d => d.IsEnabled) ?? opened[^1];
            return new PostActionTarget(dialog.Hwnd, PostActionTargetKind.NewDialog);
        }

        if (foreground != 0 && processId != 0 && foregroundProcessId == processId)
        {
            return new PostActionTarget(foreground, PostActionTargetKind.Foreground);
        }

        // The app is in the background (pattern clicks don't activate it): a blocking dialog
        // that is open in it is still the thing to look at.
        var blocking = openDialogs.LastOrDefault(d => d.ProcessId == processId && d.IsBlocking && d.IsEnabled && isOpen(d.Hwnd));
        if (blocking != null)
        {
            return new PostActionTarget(blocking.Hwnd, PostActionTargetKind.OpenDialog);
        }

        if (sourceWindow != 0 && isOpen(sourceWindow))
        {
            return new PostActionTarget(sourceWindow, PostActionTargetKind.SourceWindow);
        }

        return new PostActionTarget(0, PostActionTargetKind.None);
    }

    public static string Header(string action, string handle, string title, PostActionTargetKind kind)
    {
        var what = kind switch
        {
            PostActionTargetKind.NewDialog => "dialog it opened",
            PostActionTargetKind.OpenDialog => "open dialog",
            PostActionTargetKind.Foreground => "foreground window",
            _ => "window",
        };
        return $"--- after {action}: {what} {handle} \"{title}\" ---";
    }
}
