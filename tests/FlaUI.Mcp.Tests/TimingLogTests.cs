using System.Diagnostics;
using System.Text.Json;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class TimingLogTests
{
    [Fact]
    public void GapIsTimeBetweenResultAndNextCall()
    {
        long now = 0;
        var writer = new StringWriter();
        var log = new TimingLog(writer, clock: () => now);

        Assert.Null(log.BeginCall());
        log.EndCall("windows_click", TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(3), null, false);

        now += Stopwatch.Frequency * 2; // the model thinks for 2s
        var gap = log.BeginCall();

        Assert.NotNull(gap);
        Assert.Equal(2000, gap.Value.TotalMilliseconds, precision: 0);
        Assert.Contains("[timing] tool=windows_click exec=120ms status=3ms gap=-", writer.ToString());
    }

    [Fact]
    public void SummaryTotalsPerToolAndGaps()
    {
        long now = 0;
        var log = new TimingLog(new StringWriter(), clock: () => now) { SummaryEvery = 0 };

        log.BeginCall();
        log.EndCall("windows_snapshot", TimeSpan.FromMilliseconds(900), TimeSpan.Zero, null, false);
        now += Stopwatch.Frequency;
        var gap = log.BeginCall();
        log.EndCall("windows_click", TimeSpan.FromMilliseconds(100), TimeSpan.Zero, gap, true);
        log.BeginCall();
        log.EndCall("windows_snapshot", TimeSpan.FromMilliseconds(300), TimeSpan.Zero, TimeSpan.Zero, false);

        var summary = log.Summary();
        Assert.Contains("tool time 1300ms over 3 calls", summary);
        Assert.Contains("model turns) 1000ms over 2", summary);
        Assert.Matches(@"windows_snapshot\s+calls=2\s+total=1200ms avg=600ms max=900ms", summary);
        Assert.Contains("errors=1", summary);
        // Slowest tool first.
        Assert.True(summary.IndexOf("windows_snapshot", StringComparison.Ordinal) < summary.IndexOf("windows_click", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_WritesNothing()
    {
        var writer = new StringWriter();
        var log = new TimingLog(writer, enabled: false);

        log.EndCall("windows_click", TimeSpan.FromMilliseconds(5), TimeSpan.Zero, null, false);
        log.Detail("snapshot", TimeSpan.FromMilliseconds(5));
        log.WriteSummary();

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public async Task ToolRegistry_LogsEveryCall()
    {
        var writer = new StringWriter();
        var registry = new ToolRegistry(TimeSpan.FromSeconds(1), timing: new TimingLog(writer));
        registry.RegisterTool(new EchoTool());

        await registry.ExecuteToolAsync("echo", null);
        await registry.ExecuteToolAsync("nope", null);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.StartsWith("[timing] tool=echo exec=", StringComparison.Ordinal) && l.Contains("gap=-"));
        Assert.Contains(lines, l => l.StartsWith("[timing] tool=nope ", StringComparison.Ordinal) && l.TrimEnd().EndsWith("error"));
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";

        public McpTool GetDefinition() => new() { Name = Name, Description = "", InputSchema = new { type = "object" } };

        public Task<McpToolResult> ExecuteAsync(JsonElement? arguments) => Task.FromResult(new McpToolResult
        {
            Content = new List<McpContent> { new() { Type = "text", Text = "ok" } },
        });
    }
}
