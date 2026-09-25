using System.Text.Json;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Type text into an element
/// </summary>
public class TypeTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;
    private readonly KeyboardGuard? _guard;

    public TypeTool(ElementRegistry elementRegistry, KeyboardGuard? guard = null)
    {
        _elementRegistry = elementRegistry;
        _guard = guard;
    }

    public override string Name => "windows_type";

    public override string Description => 
        
        "Type text into an element (ref, focused first) or the focused element, without clearing it (windows_fill replaces). Refused if the app isn't in the foreground.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new
            {
                type = "string",
                description = "Element ref from windows_snapshot or windows_find (e.g., 'w1e5'). If omitted, types to currently focused element."
            },
            text = new
            {
                type = "string",
                description = "Text to type"
            },
            submit = new
            {
                type = "boolean",
                description = "Press Enter after typing (default: false)"
            }
        },
        required = new[] { "text" }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var text = GetStringArgument(arguments, "text");
        if (text == null)
        {
            return Task.FromResult(ErrorResult("Missing required argument: text"));
        }

        var refId = GetStringArgument(arguments, "ref");
        var submit = GetBoolArgument(arguments, "submit", false);

        try
        {
            // Focus element if ref provided
            FlaUI.Core.AutomationElements.AutomationElement? element = null;
            if (!string.IsNullOrEmpty(refId))
            {
                element = _elementRegistry.GetElement(refId);
                if (element == null)
                {
                    return Task.FromResult(ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs."));
                }

                element.Focus();
                Thread.Sleep(50); // Small delay to ensure focus
            }

            if (_guard?.Check(element) is { } refusal)
            {
                return Task.FromResult(ErrorResult(refusal));
            }

            // Type the text
            Keyboard.Type(text);

            if (submit)
            {
                Keyboard.Press(VirtualKeyShort.ENTER);
            }

            var target = string.IsNullOrEmpty(refId) ? "focused element" : refId;
            var action = submit ? "Typed and submitted" : "Typed";
            return Task.FromResult(TextResult($"{action} \"{text}\" into {target}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to type: {ex.Message}"));
        }
    }
}

/// <summary>
/// Fill (clear and type) an element
/// </summary>
public class FillTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;
    private readonly PostActionSnapshotter? _post;
    private readonly KeyboardGuard? _guard;

    public FillTool(ElementRegistry elementRegistry, PostActionSnapshotter? post = null, KeyboardGuard? guard = null)
    {
        _elementRegistry = elementRegistry;
        _post = post;
        _guard = guard;
    }

    public override string Name => "windows_fill";

    public override string Description => 
        
        "Replace a text field's content. Ends with what changed.";

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
            value = new
            {
                type = "string",
                description = "Value to fill"
            },
            postSnapshot = new
            {
                type = "boolean",
                description = "Append a snapshot of the app's foreground window (default: true)"
            }
        },
        required = new[] { "ref", "value" }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        var value = GetStringArgument(arguments, "value");

        if (string.IsNullOrEmpty(refId))
        {
            return Task.FromResult(ErrorResult("Missing required argument: ref"));
        }
        if (value == null)
        {
            return Task.FromResult(ErrorResult("Missing required argument: value"));
        }

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return Task.FromResult(ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs."));
        }

        try
        {
            var elementName = element.Properties.Name.ValueOrDefault ?? refId;

            // Try Value pattern first
            if (element.Patterns.Value.IsSupported)
            {
                var valuePattern = element.Patterns.Value.Pattern;
                if (!valuePattern.IsReadOnly.ValueOrDefault)
                {
                    valuePattern.SetValue(value);
                    return Task.FromResult(TextResult(WithPostSnapshot($"Filled {elementName} with \"{value}\"", arguments, refId, element)));
                }
            }

            // Fall back to focus + select all + type
            element.Focus();
            Thread.Sleep(50);
            if (_guard?.Check(element) is { } refusal)
            {
                return Task.FromResult(ErrorResult($"{elementName} has no Value pattern, so filling needs the keyboard. {refusal}"));
            }
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Thread.Sleep(50);
            Keyboard.Type(value);

            return Task.FromResult(TextResult(WithPostSnapshot($"Filled {elementName} with \"{value}\"", arguments, refId, element)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to fill {refId}: {ex.Message}"));
        }
    }

    private string WithPostSnapshot(string text, JsonElement? arguments, string refId, FlaUI.Core.AutomationElements.AutomationElement element)
    {
        if (_post == null || !_post.IsEnabled(GetArgument<bool?>(arguments, "postSnapshot"))) return text;
        return PostActionSnapshotter.Append(text, _post.CaptureAfter("fill", refId, element));
    }
}
