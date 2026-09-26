using System.Text.Json;
using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core.Batch;
using PlaywrightWindows.Mcp.Core.Flows;
using PlaywrightWindows.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>onDialog answers, idle waits, virtualized items, annotated screenshots and saved flows.</summary>
[Collection("TestApps")]
public class WaitsFlowsTests : IDisposable
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly string _flowDir = Path.Combine(Path.GetTempPath(), "flaui-flows-" + Guid.NewGuid().ToString("N"));

    public WaitsFlowsTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose()
    {
        _fixture.DismissDialogs();
        if (Directory.Exists(_flowDir)) Directory.Delete(_flowDir, recursive: true);
    }

    private async Task<string> Call(ToolBase tool, object args)
    {
        var result = await _fixture.CallTool(tool, args);
        _output.WriteLine(result);
        return result;
    }

    private BatchTool Batch(FlowStore? store = null) =>
        new(_fixture.Session, _fixture.Elements, _fixture.Clicks, _fixture.Dialogs, _fixture.Pending, null, store);

    [Fact]
    public async Task OnDialog_AnswersAnExpectedPrompt_WithoutStopping()
    {
        var handle = _fixture.WinFormsHandle;
        var result = await Call(Batch(), new
        {
            onDialog = new[] { new { title = "Confirm Delete", press = "no" } },
            actions = new object[]
            {
                new { action = "select", role = "tab", name = "Dialogs", handle },
                new { action = "click", name = "Show Message Box", handle },
                new { action = "wait", until = "text_contains", automationId = "DialogStatusLabel", handle, text = "MessageBox result: No" },
            }
        });

        Assert.DoesNotContain("Stopped", result);
        Assert.Contains("onDialog: ", result);
        Assert.Contains("\"Confirm Delete\": pressed", result);
        Assert.Contains("3. wait:", result);
    }

    [Fact]
    public async Task Idle_WaitsUntilTheAppSettles()
    {
        var handle = _fixture.WinFormsHandle;
        var batch = await Call(Batch(), new
        {
            actions = new object[]
            {
                new { action = "select", role = "tab", name = "Buttons", handle },
                new { action = "wait", until = "idle", handle, stableMs = 300 },
            }
        });
        Assert.Contains("is idle", batch);

        var wait = await Call(new WaitTool(_fixture.Session, _fixture.Dialogs, _fixture.Pending), new { until = "idle", handle });
        Assert.Contains("is idle", wait);
    }

    [Fact]
    public async Task VirtualizedGridRow_IsFoundByName()
    {
        var handle = _fixture.WpfHandle;
        await Call(_fixture.CreateSetTool(), new { role = "tab", name = "Grid", handle, selected = true, postSnapshot = false });

        // Row 48 is far below the fold: WPF hasn't created it until asked.
        var result = await Call(_fixture.CreateSetTool(), new { name = "ITEM-048 Test Item 48", handle, selected = true, postSnapshot = false });
        Assert.Contains("Selected", result);
        Assert.DoesNotContain("No element matches", result);
    }

    [Fact]
    public async Task Screenshot_Annotated_ListsTheRefsItDrew()
    {
        await Call(_fixture.CreateSetTool(), new { role = "tab", name = "Buttons", handle = _fixture.WinFormsHandle, selected = true, postSnapshot = false });
        var tool = new ScreenshotTool(_fixture.Session, _fixture.Elements);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { handle = _fixture.WinFormsHandle, annotate = true, output = "file" }));
        var text = result.Content[0].Text!;
        _output.WriteLine(text);

        Assert.Contains("Refs on the image:", text);
        var click = Regex.Match(text, @"button ""Click Me"" \[ref=(w\d+e\d+)\]");
        Assert.True(click.Success, text);
        Assert.NotNull(_fixture.Elements.GetElement(click.Groups[1].Value));
        var path = Regex.Match(text, @"saved to (.+\.png)").Groups[1].Value;
        File.Delete(path);
    }

    [Fact]
    public async Task SavedFlow_RunsAgainWithOtherArguments()
    {
        var store = new FlowStore(_flowDir);
        var handle = _fixture.WinFormsHandle;
        await Call(_fixture.CreateSetTool(), new { role = "tab", name = "Forms", handle, selected = true, postSnapshot = false });
        var nameRef = TestAppFixture.FindRefInSnapshot(_fixture.TakeSnapshot(handle), "Name");
        Assert.NotNull(nameRef);

        // Uses a ref: saving must turn it into a selector.
        var save = await Call(Batch(store), new
        {
            saveAs = "fill-name",
            description = "Put a name in the Forms tab",
            @params = new { who = "Ada" },
            actions = new object[]
            {
                new { action = "fill", @ref = nameRef, value = "Ada" },
                new { action = "wait", until = "text_contains", automationId = "NameTextBox", handle, text = "Ada" },
            }
        });
        Assert.Contains("Saved flow fill-name(who)", save);
        var saved = File.ReadAllText(Path.Combine(_flowDir, "fill-name.json"));
        Assert.Contains("{{who}}", saved);
        Assert.DoesNotContain(nameRef!, saved);

        var runFlow = new RunFlowTool(store, Batch(store));
        Assert.Contains("fill-name(who)", await Call(runFlow, new { }));
        Assert.Contains("Missing argument: who", await Call(runFlow, new { name = "fill-name" }));

        var run = await Call(runFlow, new { name = "fill-name", args = new { who = "Grace" } });
        Assert.StartsWith("Flow fill-name:", run);
        Assert.DoesNotContain("Stopped", run);
        Assert.Contains("text contains \"Grace\"", run);
    }
}
