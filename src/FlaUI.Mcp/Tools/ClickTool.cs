using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Click an element by ref
/// </summary>
public class ClickTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;
    private readonly ClickExecutor _executor;
    private readonly PostActionSnapshotter? _post;

    public ClickTool(ElementRegistry elementRegistry, ClickExecutor executor, PostActionSnapshotter? post = null)
    {
        _elementRegistry = elementRegistry;
        _executor = executor;
        _post = post;
    }

    public override string Name => "windows_click";

    public override string Description =>
        
        "Click an element by ref. Never hangs: if the click opens a dialog, the dialog's handle comes back at once (the click stays pending until it closes). mode: auto (default: UI Automation pattern, else mouse), invoke, or input (a real mouse click; needs the window visible). Ends with what changed; postSnapshot=false skips that. For checkboxes, options and menus, windows_set and windows_menu are simpler.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new
            {
                type = "string",
                description = "Element ref from windows_snapshot or windows_find (e.g., 'w1e5')"
            },
            mode = new
            {
                type = "string",
                @enum = new[] { "auto", "invoke", "input" },
                description = "How to click (default: auto)"
            },
            button = new
            {
                type = "string",
                @enum = new[] { "left", "right", "middle" },
                description = "Mouse button to click (default: left)"
            },
            doubleClick = new
            {
                type = "boolean",
                description = "Whether to double-click (default: false)"
            },
            waitMs = new
            {
                type = "integer",
                description = "How long to wait for the click to return before reporting it as pending (default: 5000)"
            },
            settleMs = new
            {
                type = "integer",
                description = "After the click returns, how long to keep watching for a dialog (default: 250). 0 for clicks known not to open dialogs."
            },
            postSnapshot = new { type = new[] { "boolean", "string" }, description = PostActionModes.SchemaDescription }
        },
        required = new[] { "ref" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        if (string.IsNullOrEmpty(refId))
        {
            return ErrorResult("Missing required argument: ref");
        }

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs.");
        }

        if (!TryParseMode(GetStringArgument(arguments, "mode"), out var mode))
        {
            return ErrorResult("mode must be one of: auto, invoke, input");
        }

        var options = new ActionRunOptions();
        var waitMs = GetArgument<int?>(arguments, "waitMs");
        if (waitMs is > 0)
        {
            options = options with { WaitTimeout = TimeSpan.FromMilliseconds(Math.Min(waitMs.Value, 25_000)) };
        }
        var settleMs = GetArgument<int?>(arguments, "settleMs");
        if (settleMs is >= 0)
        {
            options = options with { SettleTime = TimeSpan.FromMilliseconds(Math.Min(settleMs.Value, 5_000)) };
        }

        var request = new ClickRequest(
            refId,
            mode,
            GetStringArgument(arguments, "button") ?? "left",
            GetBoolArgument(arguments, "doubleClick", false),
            options);

        try
        {
            var result = await _executor.ClickAsync(element, request);
            var text = result.Text;
            if (_post != null && _post.ModeFor(arguments) != PostActionMode.Off
                && (!result.IsError || result.DialogOpened))
            {
                text = PostActionSnapshotter.Append(text, _post.Capture(
                    new PostActionContext("click", result.ProcessId, result.WindowHwnd, result.NewDialogs), _post.ModeFor(arguments)));
            }
            return result.IsError ? ErrorResult(text) : TextResult(text);
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed to click {refId}: {ex.Message}");
        }
    }

    internal static bool TryParseMode(string? value, out ClickMode mode)
    {
        mode = ClickMode.Auto;
        switch (value?.ToLowerInvariant())
        {
            case null:
            case "":
            case "auto":
                return true;
            case "invoke":
                mode = ClickMode.Invoke;
                return true;
            case "input":
                mode = ClickMode.Input;
                return true;
            default:
                return false;
        }
    }
}
