using PlaywrightWindows.Mcp.Core.Dialogs;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>What action results end with.</summary>
public enum PostActionMode
{
    /// <summary>Nothing.</summary>
    Off,
    /// <summary>
    /// What changed since the window was last read. The first time a window is seen, a one-line
    /// summary (it's recorded, so the next action gets changes); a new dialog gets a small snapshot.
    /// </summary>
    Changes,
    /// <summary>Changes when there's something to compare with, otherwise a capped snapshot.</summary>
    Full,
}

/// <summary>What to show for one action. Pure.</summary>
public enum PostActionView { Summary, Changes, Snapshot }

/// <summary>
/// Settings for what's appended to action results.
/// Environment: FLAUI_MCP_POST_SNAPSHOT=changes (default) | full | off;
/// FLAUI_MCP_POST_SNAPSHOT_MAX_NODES / _MAX_CHARS bound snapshots.
/// </summary>
public sealed record PostActionOptions
{
    public PostActionMode Mode { get; init; } = PostActionMode.Changes;
    public int MaxNodes { get; init; } = 150;
    public int MaxChars { get; init; } = 8000;

    /// <summary>A dialog's first snapshot in Changes mode: dialogs are small, and they're what you act on next.</summary>
    public int DialogMaxNodes { get; init; } = 60;
    public int DialogMaxChars { get; init; } = 4000;

    /// <summary>Longest change list before it's cut short.</summary>
    public int ChangesMaxChars { get; init; } = 4000;

    /// <summary>How much of the window to read to find the changes.</summary>
    public int WalkMaxNodes { get; init; } = 5000;
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(4);

    public static PostActionOptions FromEnvironment()
    {
        var options = new PostActionOptions();
        if (PostActionModes.Parse(Environment.GetEnvironmentVariable("FLAUI_MCP_POST_SNAPSHOT")) is { } mode)
        {
            options = options with { Mode = mode };
        }
        // Older switch: no diffs meant always a snapshot.
        var diff = Environment.GetEnvironmentVariable("FLAUI_MCP_POST_SNAPSHOT_DIFF");
        if (diff != null && diff.Trim().ToLowerInvariant() is "0" or "false" or "off" or "no" && options.Mode != PostActionMode.Off)
        {
            options = options with { Mode = PostActionMode.Full };
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

public static class PostActionModes
{
    /// <summary>"changes" / "full" / "off" (and true/false, 1/0); null if unrecognised.</summary>
    public static PostActionMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "changes" or "diff" or "on" or "true" or "1" or "yes" => PostActionMode.Changes,
        "full" or "snapshot" => PostActionMode.Full,
        "off" or "none" or "false" or "0" or "no" => PostActionMode.Off,
        _ => null,
    };

    /// <summary>
    /// A tool's postSnapshot argument: absent or true means the server default (Changes if the
    /// default is Off), false means off, or a mode name.
    /// </summary>
    public static PostActionMode FromArgument(System.Text.Json.JsonElement? arguments, PostActionMode serverDefault)
    {
        if (arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object } args
            || !args.TryGetProperty("postSnapshot", out var value))
        {
            return serverDefault;
        }
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.False => PostActionMode.Off,
            System.Text.Json.JsonValueKind.True => serverDefault == PostActionMode.Off ? PostActionMode.Changes : serverDefault,
            System.Text.Json.JsonValueKind.String => Parse(value.GetString()) ?? serverDefault,
            _ => serverDefault,
        };
    }

    /// <param name="comparable">A complete earlier read of the window exists and this read finished too.</param>
    public static PostActionView Choose(PostActionMode mode, PostActionTargetKind kind, bool comparable, int changesLength, int snapshotLength)
    {
        if (mode == PostActionMode.Full)
        {
            return comparable && changesLength < snapshotLength ? PostActionView.Changes : PostActionView.Snapshot;
        }
        if (comparable) return PostActionView.Changes;
        return kind == PostActionTargetKind.NewDialog ? PostActionView.Snapshot : PostActionView.Summary;
    }

    public const string SchemaDescription =
        "What to append: \"changes\" (default: what changed; a one-line summary the first time a window is seen), " +
        "\"full\" (a capped snapshot when there's nothing to compare), or false";
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

    public static string Header(string action, string handle, string title, PostActionTargetKind kind, string? detail = null)
    {
        var what = kind switch
        {
            PostActionTargetKind.NewDialog => "dialog it opened",
            PostActionTargetKind.OpenDialog => "open dialog",
            PostActionTargetKind.Foreground => "foreground window",
            _ => "window",
        };
        return $"--- after {action}: {what} {handle} \"{title}\"{(detail != null ? $" ({detail})" : "")} ---";
    }
}
