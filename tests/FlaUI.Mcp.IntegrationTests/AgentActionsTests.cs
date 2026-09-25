using System.Diagnostics;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Forgiving selectors, state actions (windows_set), menus (windows_menu) and tables
/// (windows_read_table).
/// </summary>
[Collection("TestApps")]
public class AgentActionsTests : IDisposable
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AgentActionsTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose() => _fixture.DismissDialogs();

    private string Handle => _fixture.WinFormsHandle;

    private async Task<string> Call(ToolBase tool, object args)
    {
        var result = await _fixture.CallTool(tool, args);
        _output.WriteLine(result);
        return result;
    }

    private Task<string> SelectTab(string tab) =>
        Call(_fixture.CreateSetTool(), new { role = "tab", name = tab, handle = Handle, selected = true, postSnapshot = false });

    private async Task WaitForStatus(string text)
    {
        var result = await Call(_fixture.CreateBatchTool(postSnapshot: false), new
        {
            actions = new object[]
            {
                new { action = "wait", until = "text_contains", nameContains = "Menu:", handle = Handle, text },
            }
        });
        Assert.DoesNotContain("did not contain", result);
    }

    [Fact]
    public async Task Find_Miss_ListsClosestNames()
    {
        await SelectTab("Dialogs");
        var result = await Call(new FindTool(_fixture.CreateFinder()), new { name = "Show Mesage Box", handle = Handle });

        Assert.Contains("No element matches", result);
        Assert.Contains("Closest: ", result);
        Assert.Contains("button \"Show Message Box\" [ref=", result);
    }

    [Fact]
    public async Task Batch_LooseName_StillFindsTheElement()
    {
        await SelectTab("Buttons");
        var result = await Call(_fixture.CreateBatchTool(postSnapshot: false), new
        {
            actions = new object[] { new { action = "click", name = "click me", handle = Handle, noDialog = true } }
        });

        Assert.Contains("Click Me", result);
        Assert.DoesNotContain("No element matches", result);
    }

    [Fact]
    public async Task Set_Checked_OnlyActsWhenNeeded()
    {
        await SelectTab("Buttons");
        var set = _fixture.CreateSetTool();
        var args = new { name = "Enable the button below", handle = Handle, postSnapshot = false };

        Assert.Contains("is now checked", await Call(set, new { args.name, args.handle, args.postSnapshot, @checked = true }));
        Assert.Contains("is already checked", await Call(set, new { args.name, args.handle, args.postSnapshot, @checked = true }));
        Assert.Contains("is now unchecked", await Call(set, new { args.name, args.handle, args.postSnapshot, @checked = false }));
    }

    [Fact]
    public async Task Set_OptionInComboBox_AndSliderValue()
    {
        await SelectTab("Forms");
        var set = _fixture.CreateSetTool();

        var pick = await Call(set, new { name = "Status", role = "combobox", handle = Handle, option = "pending", postSnapshot = false });
        Assert.Contains("Selected \"Pending\"", pick);
        Assert.Contains("already selected", await Call(set, new { name = "Status", role = "combobox", handle = Handle, option = "Pending", postSnapshot = false }));
        var missing = await Call(set, new { name = "Status", role = "combobox", handle = Handle, option = "Archived", postSnapshot = false });
        Assert.Contains("Options: \"Active\", \"Inactive\", \"Pending\"", missing);
        await Call(set, new { name = "Status", role = "combobox", handle = Handle, option = "Active", postSnapshot = false });

        Assert.Contains("Set Volume to 75", await Call(set, new { name = "Volume", handle = Handle, value = 75, postSnapshot = false }));
        Assert.Contains("outside", await Call(set, new { name = "Volume", handle = Handle, value = 500, postSnapshot = false }));
        await Call(set, new { name = "Volume", handle = Handle, value = 50, postSnapshot = false });
    }

    [Fact]
    public async Task Set_Expanded_TreeItem()
    {
        await SelectTab("Trees");
        var set = _fixture.CreateSetTool();

        Assert.Contains("Collapsed Fruits", await Call(set, new { name = "Fruits", role = "treeitem", handle = Handle, expanded = false, postSnapshot = false }));
        Assert.Contains("already collapsed", await Call(set, new { name = "Fruits", role = "treeitem", handle = Handle, expanded = false, postSnapshot = false }));
        Assert.Contains("Expanded Fruits", await Call(set, new { name = "Fruits", role = "treeitem", handle = Handle, expanded = true, postSnapshot = false }));
    }

    [Fact]
    public async Task ReadTable_DataGrid_AndListView()
    {
        await SelectTab("Grid");
        var grid = await Call(_fixture.CreateReadTableTool(), new { name = "Test Data", handle = Handle, maxRows = 3 });
        var lines = grid.Split('\n');
        Assert.Contains("ID", lines[1]);
        Assert.Contains("Name", lines[1]);
        Assert.StartsWith("ref\t", lines[1]);
        Assert.Contains("ITEM-001\tTest Item 1", grid);
        Assert.DoesNotContain("ITEM-004", grid);
        Assert.Contains("call again with start=3", grid);

        var page = await Call(_fixture.CreateReadTableTool(), new { name = "Test Data", handle = Handle, start = 3, maxRows = 1 });
        Assert.Contains("ITEM-004", page);

        await SelectTab("Trees");
        var list = await Call(_fixture.CreateReadTableTool(), new { name = "Item List", handle = Handle });
        Assert.Contains("Document.pdf\tPDF\t2.4 MB", list);
        Assert.Contains("Report.docx", list);
    }

    [Fact]
    public async Task Menu_Path_InvokesTheCommand()
    {
        var result = await Call(_fixture.CreateMenuTool(), new { path = "file > recent > Report.txt", window = Handle, postSnapshot = false });
        Assert.Contains("file > recent > Report.txt", result);
        await WaitForStatus("opened Report.txt");

        var missing = await Call(_fixture.CreateMenuTool(), new { path = new[] { "File", "Recnt" }, window = Handle, postSnapshot = false });
        Assert.Contains("No menu item \"Recnt\"", missing);
        Assert.Matches("\"&?Recent\"", missing);
    }

    [Fact]
    public async Task Batch_MenuThatOpensAnExpectedDialog()
    {
        var sw = Stopwatch.StartNew();
        var result = await Call(_fixture.CreateBatchTool(postSnapshot: false), new
        {
            actions = new object[]
            {
                new { action = "menu", path = new[] { "Tools", "Show Settings" }, handle = Handle },
                new { action = "wait", until = "dialog_open" },
                new { action = "dialog_press", button = "cancel" },
                new { action = "wait", until = "text_contains", nameContains = "Menu:", handle = Handle, text = "settings Cancel" },
            }
        });
        _output.WriteLine($"{sw.ElapsedMilliseconds}ms");

        Assert.DoesNotContain("Stopped", result);
        Assert.Contains("dialog opened", result);
    }

    [Fact]
    public async Task ContextMenu_OnAnElement()
    {
        var result = await Call(_fixture.CreateMenuTool(), new { nameContains = "Menu:", handle = Handle, path = new[] { "Clear Status" }, postSnapshot = false });
        Assert.DoesNotContain("No menu item", result);
        await WaitForStatus("cleared");
    }
}
