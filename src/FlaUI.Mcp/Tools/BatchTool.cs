using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Execute multiple actions in a single call for better performance
/// </summary>
public class BatchTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly ClickExecutor _clickExecutor;

    public BatchTool(SessionManager sessionManager, ElementRegistry elementRegistry, ClickExecutor clickExecutor)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _snapshotBuilder = new SnapshotBuilder(elementRegistry);
        _clickExecutor = clickExecutor;
    }

    public override string Name => "windows_batch";

    public override string Description => 
        "Execute multiple actions in a single call. Much faster than individual calls. " +
        "Supports click, type, fill, wait and snapshot actions. Returns results for each action. " +
        "Stops early if a click opens a dialog or doesn't return, so later actions don't run against a blocked app.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            actions = new
            {
                type = "array",
                description = "List of actions to execute in order",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        action = new
                        {
                            type = "string",
                            @enum = new[] { "click", "type", "fill", "wait", "snapshot" },
                            description = "Action type"
                        },
                        @ref = new
                        {
                            type = "string",
                            description = "Element ref for click/type/fill actions"
                        },
                        text = new
                        {
                            type = "string",
                            description = "Text for type action"
                        },
                        mode = new
                        {
                            type = "string",
                            @enum = new[] { "auto", "invoke", "input" },
                            description = "Click mode for click actions (see windows_click)"
                        },
                        value = new
                        {
                            type = "string",
                            description = "Value for fill action"
                        },
                        ms = new
                        {
                            type = "integer",
                            description = "Milliseconds for wait action (default: 100)"
                        },
                        handle = new
                        {
                            type = "string",
                            description = "Window handle for snapshot action"
                        }
                    },
                    required = new[] { "action" }
                }
            },
            stopOnError = new
            {
                type = "boolean",
                description = "Stop executing if an action fails (default: true)"
            }
        },
        required = new[] { "actions" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        if (arguments == null || !arguments.Value.TryGetProperty("actions", out var actionsElement))
        {
            return ErrorResult("Missing required argument: actions");
        }

        var stopOnError = true;
        if (arguments.Value.TryGetProperty("stopOnError", out var stopProp))
        {
            stopOnError = stopProp.GetBoolean();
        }

        var results = new List<string>();
        var actions = actionsElement.EnumerateArray().ToList();

        foreach (var (actionObj, index) in actions.Select((a, i) => (a, i)))
        {
            try
            {
                var actionType = actionObj.GetProperty("action").GetString();
                if (actionType == "click")
                {
                    var click = await ExecuteClickAsync(actionObj);
                    results.Add($"{index + 1}. click: {click.Text}");
                    if (click.DialogOpened || click.StillRunning)
                    {
                        if (index + 1 < actions.Count)
                        {
                            results.Add($"Stopped after action {index + 1}: a dialog opened or the click is still pending. " +
                                        "Handle it, then continue with the remaining actions.");
                        }
                        break;
                    }
                    if (click.IsError && stopOnError)
                    {
                        results.Add($"Stopped at action {index + 1} due to error");
                        break;
                    }
                    continue;
                }

                var result = actionType switch
                {
                    "type" => ExecuteType(actionObj),
                    "fill" => ExecuteFill(actionObj),
                    "wait" => ExecuteWait(actionObj),
                    "snapshot" => ExecuteSnapshot(actionObj),
                    _ => $"Unknown action: {actionType}"
                };
                results.Add($"{index + 1}. {actionType}: {result}");
            }
            catch (Exception ex)
            {
                results.Add($"{index + 1}. ERROR: {ex.Message}");
                if (stopOnError)
                {
                    results.Add($"Stopped at action {index + 1} due to error");
                    break;
                }
            }
        }

        return TextResult(string.Join("\n", results));
    }

    private async Task<ClickResult> ExecuteClickAsync(JsonElement action)
    {
        var refId = action.TryGetProperty("ref", out var refProp) ? refProp.GetString() : null;
        if (string.IsNullOrEmpty(refId))
        {
            return new ClickResult("Missing ref", true, false, false);
        }

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return new ClickResult($"Element not found: {refId}", true, false, false);
        }

        var modeText = action.TryGetProperty("mode", out var modeProp) ? modeProp.GetString() : null;
        if (!ClickTool.TryParseMode(modeText, out var mode))
        {
            return new ClickResult("mode must be one of: auto, invoke, input", true, false, false);
        }

        return await _clickExecutor.ClickAsync(element, new ClickRequest(refId, mode));
    }

    private string ExecuteType(JsonElement action)
    {
        var text = action.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        if (string.IsNullOrEmpty(text))
        {
            return "Missing text";
        }

        var refId = action.TryGetProperty("ref", out var refProp) ? refProp.GetString() : null;
        if (!string.IsNullOrEmpty(refId))
        {
            var element = _elementRegistry.GetElement(refId);
            if (element == null)
            {
                return $"Element not found: {refId}";
            }
            element.Focus();
            Thread.Sleep(30);
        }

        Keyboard.Type(text);
        return $"Typed \"{text}\"";
    }

    private string ExecuteFill(JsonElement action)
    {
        var refId = action.TryGetProperty("ref", out var refProp) ? refProp.GetString() : null;
        var value = action.TryGetProperty("value", out var valProp) ? valProp.GetString() : null;

        if (string.IsNullOrEmpty(refId) || value == null)
        {
            return "Missing ref or value";
        }

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return $"Element not found: {refId}";
        }

        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(value);
            return $"Filled with \"{value}\"";
        }

        // Fallback
        element.Focus();
        Thread.Sleep(30);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(30);
        Keyboard.Type(value);
        return $"Filled with \"{value}\"";
    }

    private string ExecuteWait(JsonElement action)
    {
        var ms = action.TryGetProperty("ms", out var msProp) ? msProp.GetInt32() : 100;
        Thread.Sleep(ms);
        return $"Waited {ms}ms";
    }

    private string ExecuteSnapshot(JsonElement action)
    {
        var handle = action.TryGetProperty("handle", out var handleProp) ? handleProp.GetString() : null;
        
        Window? window = null;
        if (!string.IsNullOrEmpty(handle))
        {
            window = _sessionManager.GetWindow(handle);
            if (window == null)
            {
                return $"Window not found: {handle}";
            }
        }
        else
        {
            // Get focused window
            var focusedElement = _sessionManager.Automation.FocusedElement();
            if (focusedElement != null)
            {
                var current = focusedElement;
                while (current != null)
                {
                    if (current.Properties.ControlType.ValueOrDefault == FlaUI.Core.Definitions.ControlType.Window)
                    {
                        window = current.AsWindow();
                        handle = _sessionManager.RegisterWindow(window);
                        break;
                    }
                    current = current.Parent;
                }
            }
        }

        if (window == null)
        {
            return "No window found";
        }

        var snapshot = _snapshotBuilder.BuildSnapshot(handle!, window);
        return $"\n{snapshot}";
    }
}
