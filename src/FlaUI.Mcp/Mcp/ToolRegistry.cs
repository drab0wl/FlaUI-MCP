using System.Diagnostics;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core.Diagnostics;

namespace PlaywrightWindows.Mcp;

/// <summary>
/// Registry for MCP tools - maps tool names to handlers
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly TimeSpan _toolTimeout;
    private readonly IToolResultAnnotator? _annotator;
    private readonly TimingLog _timing;

    public ToolRegistry(TimeSpan? toolTimeout = null, IToolResultAnnotator? annotator = null, TimingLog? timing = null)
    {
        _annotator = annotator;
        _timing = timing ?? TimingLog.Shared;
        _toolTimeout = toolTimeout ?? TimeSpan.FromSeconds(30);
        if (_toolTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(toolTimeout), "Tool timeout must be greater than zero.");
        }
    }

    public void RegisterTool(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public List<McpTool> GetToolDefinitions()
    {
        return _tools.Values.Select(t => t.GetDefinition()).ToList();
    }

    public async Task<McpToolResult> ExecuteToolAsync(string name, JsonElement? arguments)
    {
        var gap = _timing.BeginCall();
        var sw = Stopwatch.StartNew();
        var result = await ExecuteCoreAsync(name, arguments);
        var execution = sw.Elapsed;
        var annotated = Annotate(name, result);
        _timing.EndCall(name, execution, sw.Elapsed - execution, gap, result.IsError == true);
        return annotated;
    }

    private McpToolResult Annotate(string name, McpToolResult result)
    {
        if (_annotator == null) return result;

        string? footer;
        try
        {
            footer = _annotator.GetFooter(name);
        }
        catch
        {
            return result;
        }
        if (string.IsNullOrEmpty(footer)) return result;

        var content = new List<McpContent>(result.Content);
        var first = content.FindIndex(c => c.Type == "text");
        if (first >= 0)
        {
            content[first] = content[first] with { Text = $"{content[first].Text}\n\n{footer}" };
        }
        else
        {
            content.Add(new McpContent { Type = "text", Text = footer });
        }
        return result with { Content = content };
    }

    private async Task<McpToolResult> ExecuteCoreAsync(string name, JsonElement? arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Unknown tool: {name}" }
                },
                IsError = true
            };
        }

        try
        {
            var toolTask = Task.Run(() => tool.ExecuteAsync(arguments));
            var timeoutTask = Task.Delay(_toolTimeout);

            if (await Task.WhenAny(toolTask, timeoutTask) == timeoutTask)
            {
                _ = toolTask.ContinueWith(
                    task => { _ = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);

                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new()
                        {
                            Type = "text",
                            Text = $"Tool '{name}' timed out after {(int)_toolTimeout.TotalMilliseconds}ms. " +
                                   "A modal dialog or blocked UI Automation provider may still be running in the background. " +
                                   "Use windows_dialogs to find open dialogs, and windows_dialog (Win32) or " +
                                   "windows_click with mode=input to dismiss them without UI Automation."
                        }
                    },
                    IsError = true
                };
            }

            return await toolTask;
        }
        catch (Exception ex)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {ex.Message}" }
                },
                IsError = true
            };
        }
    }
}

/// <summary>
/// Supplies a status block appended to every tool result (open dialogs, pending actions).
/// </summary>
public interface IToolResultAnnotator
{
    /// <returns>Text to append, or null for nothing.</returns>
    string? GetFooter(string toolName);
}

/// <summary>
/// Interface for MCP tools
/// </summary>
public interface ITool
{
    string Name { get; }
    McpTool GetDefinition();
    Task<McpToolResult> ExecuteAsync(JsonElement? arguments);
}

/// <summary>
/// Base class for tools with common utilities
/// </summary>
public abstract class ToolBase : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract object InputSchema { get; }

    public McpTool GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema
    };

    public abstract Task<McpToolResult> ExecuteAsync(JsonElement? arguments);

    protected static McpToolResult TextResult(string text) => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "text", Text = text }
        }
    };

    protected static McpToolResult ErrorResult(string message) => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "text", Text = message }
        },
        IsError = true
    };

    protected static McpToolResult ImageResult(byte[] imageData, string mimeType = "image/png") => new()
    {
        Content = new List<McpContent>
        {
            new() 
            { 
                Type = "image", 
                Data = Convert.ToBase64String(imageData),
                MimeType = mimeType
            }
        }
    };

    protected T? GetArgument<T>(JsonElement? arguments, string name)
    {
        if (arguments == null) return default;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return default;
        return JsonSerializer.Deserialize<T>(prop.GetRawText(), McpProtocol.JsonOptions);
    }

    protected string? GetStringArgument(JsonElement? arguments, string name)
    {
        if (arguments == null) return null;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return null;
        return prop.GetString();
    }

    protected bool GetBoolArgument(JsonElement? arguments, string name, bool defaultValue = false)
    {
        if (arguments == null) return defaultValue;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.GetBoolean();
    }
}
