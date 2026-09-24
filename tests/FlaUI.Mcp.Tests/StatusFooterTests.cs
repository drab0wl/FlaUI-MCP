using System.Text.Json;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class StatusFooterTests
{
    private static readonly DialogInfo Modal = new(0x20, 7, 0x10, "#32770", "Confirm", true, true);
    private static readonly DialogInfo Modeless = new(0x30, 7, 0x10, "WindowsForms10.Window", "Find", false, true);

    [Fact]
    public void NothingToReport_ReturnsNull()
    {
        var footer = StatusFooter.Build(
            new[] { Modeless }, _ => "w9",
            Array.Empty<PendingOperation>(), Array.Empty<PendingOperation>());

        Assert.Null(footer);
    }

    [Fact]
    public void ListsBlockingDialogsRunningAndFinishedOps()
    {
        var registry = new PendingOperationRegistry();
        var running = registry.Register("click \"Delete\"", 7, new TaskCompletionSource<string>().Task);
        var done = registry.Register("click \"Save\"", 7, Task.FromResult("Invoked Save"));

        var footer = StatusFooter.Build(
            new[] { Modal, Modeless }, d => d.Hwnd == 0x20 ? "w5" : "w6",
            new[] { running }, new[] { done }, running.StartedUtc.AddSeconds(3));

        Assert.NotNull(footer);
        Assert.StartsWith(StatusFooter.Header, footer);
        Assert.Contains("w5 \"Confirm\" [modal] [win32] (pid 7)", footer);
        Assert.DoesNotContain("Find", footer);
        Assert.Contains("op1: click \"Delete\" (running 3.0s)", footer);
        Assert.Contains("op2: click \"Save\" -> Invoked Save", footer);
    }

    [Fact]
    public async Task Registry_AppendsFooterToTextResults()
    {
        var registry = new ToolRegistry(TimeSpan.FromSeconds(1), new FixedAnnotator("--- status ---\nOpen dialogs: w5"));
        registry.RegisterTool(new EchoTool());

        var result = await registry.ExecuteToolAsync("echo", arguments: null);

        Assert.Single(result.Content);
        Assert.Equal("hello\n\n--- status ---\nOpen dialogs: w5", result.Content[0].Text);
    }

    [Fact]
    public async Task Registry_ThrowingAnnotator_LeavesResultAlone()
    {
        var registry = new ToolRegistry(TimeSpan.FromSeconds(1), new ThrowingAnnotator());
        registry.RegisterTool(new EchoTool());

        var result = await registry.ExecuteToolAsync("echo", arguments: null);

        Assert.Equal("hello", result.Content[0].Text);
    }

    private sealed class FixedAnnotator(string text) : IToolResultAnnotator
    {
        public string? GetFooter(string toolName) => text;
    }

    private sealed class ThrowingAnnotator : IToolResultAnnotator
    {
        public string? GetFooter(string toolName) => throw new Exception("boom");
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";

        public McpTool GetDefinition() => new() { Name = Name, InputSchema = new { type = "object" } };

        public Task<McpToolResult> ExecuteAsync(JsonElement? arguments) =>
            Task.FromResult(new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "hello" } }
            });
    }
}
