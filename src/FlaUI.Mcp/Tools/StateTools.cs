using System.Text.Json;
using FlaUI.Core.AutomationElements;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Batch;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Core.Tables;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>A tool's target: a ref, or a selector (name / nameContains / automationId / role / handle).</summary>
internal static class ToolTarget
{
    public static object SelectorProperties(string what) => new
    {
        @ref = new { type = "string", description = $"Ref of the {what} (from a snapshot or windows_find)" },
        name = new { type = "string", description = "Or find it by name (matched loosely: \"Save As\" finds \"Save As...\")" },
        nameContains = new { type = "string" },
        automationId = new { type = "string" },
        role = new { type = "string", description = "Role as shown in snapshots" },
        handle = new { type = "string", description = "Window to search (default: the tracked apps' windows)" },
    };

    public static (AutomationElement? Element, string? Ref, string? Error) Resolve(
        JsonElement? arguments, ElementRegistry elements, ElementFinder finder, bool required = true)
    {
        if (arguments is { ValueKind: JsonValueKind.Object } args)
        {
            if (args.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } refId)
            {
                var element = elements.GetElement(refId);
                return element == null
                    ? (null, null, $"Element not found: {refId}. Run windows_snapshot or windows_find to refresh element refs.")
                    : (element, refId, null);
            }

            var selector = ElementSelector.FromStep(args, out var error);
            if (error != null) return (null, null, error);
            if (selector != null)
            {
                if (selector.Handle == ElementSelector.LastDialog)
                {
                    return (null, null, "\"$dialog\" only works inside windows_batch. Use the dialog's handle.");
                }
                var found = finder.Find(selector, null, out var findError, forgiving: true);
                return found == null ? (null, null, findError) : (found.Element, found.Ref, null);
            }
        }
        return required ? (null, null, "Give a ref, or name / nameContains / automationId / role.") : (null, null, null);
    }

    public static McpToolResult Result(ClickResult result, PostActionSnapshotter? post, JsonElement? arguments, string action)
    {
        var text = result.Text;
        if (post != null && post.ModeFor(arguments) is var mode && mode != PostActionMode.Off && (!result.IsError || result.DialogOpened))
        {
            text = PostActionSnapshotter.Append(text, post.Capture(
                new PostActionContext(action, result.ProcessId, result.WindowHwnd, result.NewDialogs), mode));
        }
        return new McpToolResult
        {
            Content = new List<McpContent> { new() { Type = "text", Text = text } },
            IsError = result.IsError ? true : null,
        };
    }
}

/// <summary>
/// Put an element into a state: checked, expanded, an option selected, a value set. Does
/// nothing when it's already so, which makes it safe to repeat (a click would flip it back).
/// </summary>
public class SetTool : ToolBase
{
    private readonly ElementRegistry _elements;
    private readonly ElementFinder _finder;
    private readonly StateActions _state;
    private readonly PostActionSnapshotter? _post;

    public SetTool(ElementRegistry elements, ElementFinder finder, StateActions state, PostActionSnapshotter? post = null)
    {
        _elements = elements;
        _finder = finder;
        _state = state;
        _post = post;
    }

    public override string Name => "windows_set";

    public override string Description =>
        
        "Put an element into a state, only acting if needed (safe to repeat): checked, expanded, selected=true, option (an item in a combo box, list, tree or tab list), or value (a number for sliders, text for text boxes). Target by ref or name/automationId/role. Never hangs on dialogs; ends with what changed.";

    public override object InputSchema => new
    {
        type = "object",
        properties = Merge(ToolTarget.SelectorProperties("element"), new
        {
            @checked = new { type = "boolean" },
            expanded = new { type = "boolean" },
            selected = new { type = "boolean", description = "true: select the element itself" },
            option = new { type = "string", description = "Item to select inside the element" },
            value = new { type = new[] { "string", "number" } },
            postSnapshot = new { type = new[] { "boolean", "string" }, description = PostActionModes.SchemaDescription },
        })
    };

    internal static Dictionary<string, object?> Merge(object a, object b)
    {
        var result = new Dictionary<string, object?>();
        foreach (var o in new[] { a, b })
        {
            foreach (var p in o.GetType().GetProperties()) result[p.Name == "ref" ? "ref" : p.Name] = p.GetValue(o);
        }
        return result;
    }

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var (element, refId, error) = ToolTarget.Resolve(arguments, _elements, _finder);
        if (error != null) return ErrorResult(error);

        var isChecked = GetArgument<bool?>(arguments, "checked");
        var expanded = GetArgument<bool?>(arguments, "expanded");
        var selected = GetArgument<bool?>(arguments, "selected");
        var option = GetStringArgument(arguments, "option");
        string? value = null;
        if (arguments?.TryGetProperty("value", out var v) == true)
        {
            value = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        }

        var given = new object?[] { isChecked, expanded, selected == true ? true : null, option, value }.Count(x => x != null);
        if (given != 1)
        {
            return ErrorResult("Give exactly one of: checked, expanded, selected=true, option, value.");
        }

        try
        {
            var result = isChecked is { } c ? await _state.SetCheckedAsync(element!, refId!, c)
                : expanded is { } e ? await _state.SetExpandedAsync(element!, refId!, e)
                : option != null ? await _state.SelectAsync(element!, refId!, option)
                : selected == true ? await _state.SelectAsync(element!, refId!, null)
                : await _state.SetValueAsync(element!, refId!, value!);
            return ToolTarget.Result(result, _post, arguments, "set");
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed: {ex.Message}");
        }
    }
}

/// <summary>Open a menu path in one call.</summary>
public class MenuTool : ToolBase
{
    private readonly SessionManager _sessions;
    private readonly ElementRegistry _elements;
    private readonly ElementFinder _finder;
    private readonly MenuActions _menus;
    private readonly PostActionSnapshotter? _post;

    public MenuTool(SessionManager sessions, ElementRegistry elements, ElementFinder finder, MenuActions menus, PostActionSnapshotter? post = null)
    {
        _sessions = sessions;
        _elements = elements;
        _finder = finder;
        _menus = menus;
        _post = post;
    }

    public override string Name => "windows_menu";

    public override string Description =>
        
        "Run a menu command by path, e.g. \"File > Save As...\"; with a ref or name/automationId/role, uses that element's context menu. Names match loosely. Returns a dialog's handle if one opens. mode=input clicks the items with the mouse if the default fails.";

    public override object InputSchema => new
    {
        type = "object",
        properties = SetTool.Merge(ToolTarget.SelectorProperties("element whose context menu to open"), new
        {
            path = new { type = new[] { "array", "string" }, items = new { type = "string" }, description = "Menu items in order" },
            window = new { type = "string", description = "Window whose menu bar to use (default: the app's foreground window)" },
            mode = new { type = "string", @enum = new[] { "auto", "input" }, description = "Default auto" },
            postSnapshot = new { type = new[] { "boolean", "string" }, description = PostActionModes.SchemaDescription },
        }),
        required = new[] { "path" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var path = arguments is { } a ? MenuPath.Parse(a) : null;
        if (path is not { Count: > 0 }) return ErrorResult("path is required, e.g. [\"File\", \"Save As...\"].");
        if (!ClickTool.TryParseMode(GetStringArgument(arguments, "mode"), out var mode)) return ErrorResult("mode must be auto or input");

        var (element, refId, error) = ToolTarget.Resolve(arguments, _elements, _finder, required: false);
        if (error != null) return ErrorResult(error);

        string? handle;
        if (refId != null)
        {
            handle = ElementRegistry.WindowHandleOf(refId);
        }
        else
        {
            handle = GetStringArgument(arguments, "window");
            if (handle == null)
            {
                var foreground = NativeMethods.GetForegroundWindow();
                var pid = foreground != 0 ? NativeMethods.GetProcessId(foreground) : 0;
                if (pid != 0 && _sessions.TrackedProcessIds.Contains(pid)) handle = _sessions.RegisterNativeWindow(foreground, pid);
            }
        }
        if (handle == null) return ErrorResult("Pass window (a window handle): no app being automated is in the foreground.");
        var window = _sessions.GetWindow(handle);
        if (window == null) return ErrorResult($"Window not found: {handle}");

        try
        {
            var result = await _menus.OpenAsync(handle, window, path, mode, element != null ? (element, refId!) : null);
            return ToolTarget.Result(result, _post, arguments, "menu");
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed to open {MenuPath.Describe(path)}: {ex.Message}");
        }
    }
}

/// <summary>Read a grid or list as rows and columns.</summary>
public class ReadTableTool : ToolBase
{
    private const int DefaultRows = 50;
    private const int MaxRows = 500;

    private readonly ElementRegistry _elements;
    private readonly ElementFinder _finder;
    private readonly TableReader _reader;

    public ReadTableTool(ElementRegistry elements, ElementFinder finder)
    {
        _elements = elements;
        _finder = finder;
        _reader = new TableReader(elements);
    }

    public override string Name => "windows_read_table";

    public override string Description =>
        
        "Read a grid, list view or table as tab-separated rows: headers, then one line per row starting with its ref. Page with start/maxRows.";

    public override object InputSchema => new
    {
        type = "object",
        properties = SetTool.Merge(ToolTarget.SelectorProperties("grid, list or table"), new
        {
            start = new { type = "integer", description = "First row, 0-based (default 0)" },
            maxRows = new { type = "integer", description = $"Default {DefaultRows}, max {MaxRows}" },
        })
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var (element, refId, error) = ToolTarget.Resolve(arguments, _elements, _finder);
        if (error != null) return Task.FromResult(ErrorResult(error));
        var handle = ElementRegistry.WindowHandleOf(refId!);
        if (handle == null) return Task.FromResult(ErrorResult($"Can't tell which window {refId} belongs to."));

        var start = Math.Max(0, GetArgument<int?>(arguments, "start") ?? 0);
        var max = Math.Clamp(GetArgument<int?>(arguments, "maxRows") ?? DefaultRows, 1, MaxRows);
        try
        {
            return Task.FromResult(TextResult(_reader.Read(element!, refId!, handle, start, max)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to read {refId}: {ex.Message}"));
        }
    }
}
