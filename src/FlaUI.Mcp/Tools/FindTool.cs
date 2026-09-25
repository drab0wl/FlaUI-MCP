using System.Text.Json;
using PlaywrightWindows.Mcp.Core.Batch;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Search for elements instead of reading a whole snapshot.
/// </summary>
public class FindTool : ToolBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 200;

    private readonly ElementFinder _finder;

    public FindTool(ElementFinder finder)
    {
        _finder = finder;
    }

    public override string Name => "windows_find";

    public override string Description =>
        
        "Find elements by name, nameContains, automationId and/or role. Returns their refs, each with the named elements it sits in. Much cheaper than a snapshot on big windows. A miss lists the closest names.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "Exact name" },
            nameContains = new { type = "string", description = "Substring of the name (case-insensitive)" },
            automationId = new { type = "string" },
            role = new { type = "string", description = "Role as shown in snapshots: button, textbox, menuitem, tab, treeitem, ..." },
            handle = new { type = "string", description = "Window to search (default: the tracked apps' windows)" },
            limit = new { type = "integer", description = $"Maximum matches (default {DefaultLimit}, max {MaxLimit})" }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        if (arguments == null)
        {
            return Task.FromResult(ErrorResult("Give name, nameContains, automationId or role."));
        }

        var selector = ElementSelector.FromStep(arguments.Value, out var error);
        if (error != null) return Task.FromResult(ErrorResult(error));
        if (selector == null) return Task.FromResult(ErrorResult("Give name, nameContains, automationId or role."));
        if (selector.Handle == ElementSelector.LastDialog)
        {
            return Task.FromResult(ErrorResult("\"$dialog\" only works inside windows_batch. Use the dialog's handle."));
        }

        var limit = Math.Clamp(GetArgument<int?>(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
        try
        {
            var found = _finder.FindAll(selector, limit, out var more, out var findError);
            if (found.Count == 0) return Task.FromResult(ErrorResult(findError ?? $"No element matches {selector.Describe()}."));

            var text = FindFormat.Render(found.Select(f => (f.WindowHandle, f.Line, f.Path)).ToList(), more, limit);
            if (findError != null) text += $"\n({findError})";
            return Task.FromResult(TextResult(text));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to search: {ex.Message}"));
        }
    }
}
