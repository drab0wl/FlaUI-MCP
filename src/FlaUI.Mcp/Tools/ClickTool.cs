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

    public ClickTool(ElementRegistry elementRegistry, ClickExecutor executor)
    {
        _elementRegistry = elementRegistry;
        _executor = executor;
    }

    public override string Name => "windows_click";

    public override string Description =>
        "Click an element by its ref (from windows_snapshot). Never hangs on modal dialogs: if the click " +
        "opens one, this returns right away with the dialog's window handle (snapshot it and act on it " +
        "like any window) and the click becomes a pending operation that finishes when the dialog closes. " +
        "mode=auto (default) uses UI Automation patterns (Invoke/Toggle/Select), falling back to the mouse. " +
        "mode=input sends a real mouse click at the element (needs the window visible and foreground; best " +
        "for buttons that open dialogs in WinForms/Win32 apps, because no UI Automation call is left waiting). " +
        "mode=invoke forces the pattern path.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new
            {
                type = "string",
                description = "Element ref from windows_snapshot (e.g., 'w1e5')"
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
            }
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

        var request = new ClickRequest(
            refId,
            mode,
            GetStringArgument(arguments, "button") ?? "left",
            GetBoolArgument(arguments, "doubleClick", false),
            options);

        try
        {
            var result = await _executor.ClickAsync(element, request);
            return result.IsError ? ErrorResult(result.Text) : TextResult(result.Text);
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
