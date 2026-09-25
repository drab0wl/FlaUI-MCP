using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp.Core.Batch;
using PlaywrightWindows.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Change reports after actions, windows_find, subtree snapshots and batch keys.
/// </summary>
[Collection("TestApps")]
public class FewerRoundTripsTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public FewerRoundTripsTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private FindTool CreateFindTool() =>
        new(new ElementFinder(_fixture.Session, _fixture.Elements, _fixture.Dialogs, _fixture.Pending));

    private async Task SelectTab(string handle, string tab)
    {
        await _fixture.CallTool(_fixture.CreateBatchTool(postSnapshot: false), new
        {
            actions = new object[]
            {
                new { action = "click", role = "tab", name = tab, handle, noDialog = true },
                new { action = "wait", ms = 200 },
            }
        });
    }

    [Fact]
    public async Task Click_AfterAFullSnapshot_ReportsOnlyWhatChanged()
    {
        var handle = _fixture.WinFormsHandle;
        await SelectTab(handle, "Buttons");
        var snapshot = _fixture.TakeSnapshot(handle); // the baseline
        var checkbox = TestAppFixture.FindRefInSnapshot(snapshot, "Enable the button below");
        var button = TestAppFixture.FindRefInSnapshot(snapshot, "Conditional Button");
        Assert.NotNull(checkbox);
        Assert.NotNull(button);

        var click = new ClickTool(_fixture.Elements, _fixture.Clicks, _fixture.CreatePostAction());
        var result = await _fixture.CallTool(click, new { @ref = checkbox, settleMs = 0 });
        _output.WriteLine(result);

        Assert.Matches(@"--- after click: .* \(changes: \d+ added, \d+ removed, \d+ changed\) ---", result);
        Assert.Contains($"checkbox \"Enable the button below\" [ref={checkbox}] [checked]", result);
        // The button got enabled: it's listed as changed, with the same ref.
        Assert.Matches($@"button ""Conditional Button"" \[ref={button}\]\s+\(was: button ""Conditional Button"" \[ref={button}\] \[disabled\]\)", result);
        Assert.DoesNotContain("Click Me", result); // unchanged, so not repeated

        // Nothing changes on a second look.
        var again = await _fixture.CallTool(click, new { @ref = checkbox, postSnapshot = false });
        _output.WriteLine(again);
    }

    [Fact]
    public async Task Find_ReturnsRefsWithWhereTheyAre()
    {
        var handle = _fixture.WinFormsHandle;
        await SelectTab(handle, "Dialogs");

        var result = await _fixture.CallTool(CreateFindTool(), new { name = "Show Message Box", handle });
        _output.WriteLine(result);

        var match = Regex.Match(result, $@"- button ""Show Message Box"" \[ref=({handle}e\d+)\]");
        Assert.True(match.Success, result);
        Assert.Contains("(in ", result);
        Assert.NotNull(_fixture.Elements.GetElement(match.Groups[1].Value));

        var many = await _fixture.CallTool(CreateFindTool(), new { role = "button", handle, limit = 2 });
        _output.WriteLine(many);
        Assert.Contains("more than 2 matches", many);

        var none = await _fixture.CallTool(CreateFindTool(), new { name = "No Such Thing", handle });
        Assert.Contains("No element matches", none);
    }

    [Fact]
    public async Task SnapshotOfARef_ShowsOnlyThatPart_AndKeepsOtherRefs()
    {
        var handle = _fixture.WinFormsHandle;
        await SelectTab(handle, "Forms");
        var full = _fixture.TakeSnapshot(handle);
        var tabs = TestAppFixture.FindRefInSnapshot(full, "Buttons"); // a tab outside the Forms page
        var name = TestAppFixture.FindRefInSnapshot(full, "Name");
        Assert.NotNull(tabs);
        Assert.NotNull(name);

        var found = await _fixture.CallTool(CreateFindTool(), new { automationId = "FormsTab", handle });
        var pageRef = Regex.Match(found, @"\[ref=(w\d+e\d+)\]").Groups[1].Value;
        var part = await _fixture.CallTool(new SnapshotTool(_fixture.Session, _fixture.Elements, _fixture.Pending),
            new { @ref = pageRef, compact = true });
        _output.WriteLine(part);

        Assert.Contains($"[ref={name}]", part);
        Assert.DoesNotContain("\"Buttons\"", part);
        Assert.NotNull(_fixture.Elements.GetElement(tabs));
    }

    [Fact]
    public async Task Batch_Keys_EditAField()
    {
        var handle = _fixture.WinFormsHandle;
        await SelectTab(handle, "Forms");

        var result = await _fixture.CallTool(_fixture.CreateBatchTool(postSnapshot: false), new
        {
            actions = new object[]
            {
                new { action = "fill", automationId = "NameTextBox", handle, value = "hello world" },
                new { action = "keys", automationId = "NameTextBox", handle, keys = new[] { "End", "Ctrl+Shift+Left", "Delete" } },
                new { action = "type", text = "there" },
                new { action = "wait", until = "text_contains", automationId = "NameTextBox", handle, text = "hello there" },
            }
        });
        _output.WriteLine(result);

        Assert.Contains("2. keys: ", result);
        Assert.Contains("Sent End, Ctrl+Shift+Left, Delete", result);
        Assert.DoesNotContain("Stopped", result);
    }

    [Fact]
    public async Task Batch_Keys_UnknownKey_SendsNothing()
    {
        var result = await _fixture.CallTool(_fixture.CreateBatchTool(postSnapshot: false), new
        {
            actions = new object[] { new { action = "keys", keys = new[] { "Tab", "Hyper+Q" } } }
        });
        _output.WriteLine(result);
        Assert.Contains("Unsupported key: Hyper", result);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("preview")]
    public async Task Screenshot_ToAFile_ReturnsThePath(string output)
    {
        var tool = new ScreenshotTool(_fixture.Session, _fixture.Elements);
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { handle = _fixture.WinFormsHandle, output });
        var result = await tool.ExecuteAsync(args);
        var text = result.Content[0].Text!;
        _output.WriteLine(text);

        var match = Regex.Match(text, @"Screenshot saved to (.+\.png) \((\d+)x(\d+)");
        Assert.True(match.Success, text);
        Assert.True(File.Exists(match.Groups[1].Value));
        if (output == "file")
        {
            Assert.Single(result.Content);
        }
        else
        {
            Assert.Equal("image/jpeg", result.Content[1].MimeType);
            Assert.NotEmpty(Convert.FromBase64String(result.Content[1].Data!));
        }
        File.Delete(match.Groups[1].Value);
    }
}
