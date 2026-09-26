namespace PlaywrightWindows.Mcp;

/// <summary>
/// The "instructions" the server sends at initialize: how the tools fit together. Clients
/// put it in the model's system prompt, so it stays short. FLAUI_MCP_INSTRUCTIONS=off sends
/// none; a file path sends that file's text instead.
/// </summary>
public static class ServerInstructions
{
    public const string Default =
        """
        Drive Windows desktop apps through UI Automation.
        - Start with windows_launch or windows_list_windows to get a window handle (w1).
        - Look: windows_find for specific elements; windows_snapshot for an overview (compact=true on big windows). Refs like w1e5 stay valid across snapshots.
        - Act: windows_click; windows_set for checkboxes, options, sliders, tree items (no-op if already set); windows_menu for menus; windows_fill / windows_type for text; windows_read_table for grids.
        - Action results end with what changed (a summary the first time), so don't snapshot again to check.
        - For several steps use one windows_batch, targeting elements by name/automationId/role. After a step that opens a dialog add {"action":"wait","until":"dialog_open"}; for prompts that may pop up pass onDialog answers; after slow work wait until=idle.
        - Dialogs never hang a call: the result gives the dialog's handle. If UI Automation is blocked, use windows_dialog.
        - A name that matches nothing lists the closest names: retry with one instead of snapshotting.
        - Keys only go to the automated app when it's in the foreground.
        - Repeated tasks: windows_run_flow lists saved flows; add saveAs to a batch that worked.
        - windows_screenshot: output=file returns a path, not image data; annotate=true draws refs on it.
        """;

    /// <summary>The instructions to send, or null for none.</summary>
    public static string? Resolve(string? setting, Func<string, bool>? fileExists = null, Func<string, string>? readFile = null)
    {
        fileExists ??= File.Exists;
        readFile ??= File.ReadAllText;
        var value = setting?.Trim();
        if (string.IsNullOrEmpty(value)) return Default;
        if (value.ToLowerInvariant() is "off" or "0" or "false" or "none") return null;
        if (fileExists(value))
        {
            var text = readFile(value).Trim();
            return text.Length > 0 ? text : null;
        }
        return Default;
    }

    public static string? FromEnvironment() => Resolve(Environment.GetEnvironmentVariable("FLAUI_MCP_INSTRUCTIONS"));
}
