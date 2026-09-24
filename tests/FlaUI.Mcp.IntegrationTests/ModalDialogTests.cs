using System.Diagnostics;
using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Clicking buttons that open modal dialogs must return promptly with the dialog's handle,
/// and the dialog must then be drivable, via UI Automation or the Win32 fallback.
/// </summary>
[Collection("TestApps")]
public class ModalDialogTests : IDisposable
{
    // Well under the old behavior (a 30s tool timeout).
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(4);

    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ModalDialogTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose() => _fixture.DismissDialogs();

    [Fact]
    public async Task WinForms_MessageBox_ClickReturnsPromptly_AndWin32FallbackDrivesIt()
    {
        var buttonRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Dialogs", "Show Message Box");

        var (result, elapsed) = await Timed(() => _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = buttonRef }));
        _output.WriteLine($"click ({elapsed.TotalMilliseconds:0}ms): {result}");
        Assert.True(elapsed < Prompt, $"Click took {elapsed}");
        var dialog = ExtractHandle(result, "Confirm Delete");

        var dialogTool = new NativeDialogTool(_fixture.Session);
        var read = await _fixture.CallTool(dialogTool, new { handle = dialog, action = "read" });
        _output.WriteLine(read);
        Assert.Contains("Delete the selected item?", read);
        Assert.Matches("button \"&?Yes\"", read); // the accelerator may or may not be present

        var press = await _fixture.CallTool(dialogTool, new { handle = dialog, action = "press", button = "no" });
        _output.WriteLine(press);
        Assert.Contains("The dialog closed", press);

        await WaitForText(_fixture.WinFormsHandle, "MessageBox result: No");
        await WaitForPendingToFinish();
    }

    [Fact]
    public async Task WinForms_ModalForm_InputClick_ThenDriveDialogWithUia()
    {
        var buttonRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Dialogs", "Open Modal Dialog");

        // Mouse input leaves no UIA call outstanding, so the dialog's UIA tree is fully usable.
        var (result, elapsed) = await Timed(() =>
            _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = buttonRef, mode = "input" }));
        _output.WriteLine($"click ({elapsed.TotalMilliseconds:0}ms): {result}");
        Assert.True(elapsed < Prompt, $"Click took {elapsed}");
        Assert.Contains("mouse input", result);
        var dialog = ExtractHandle(result, "Test Modal Dialog");

        var snapshot = await _fixture.CallTool(new SnapshotTool(_fixture.Session, _fixture.Elements, _fixture.Pending), new { handle = dialog });
        _output.WriteLine(snapshot);
        var inputRef = TestAppFixture.FindRefInSnapshot(snapshot, "Your name");
        var okRef = TestAppFixture.FindRefInSnapshot(snapshot, "OK");
        Assert.NotNull(inputRef);
        Assert.NotNull(okRef);

        await _fixture.CallTool(new FillTool(_fixture.Elements), new { @ref = inputRef, value = "Ada" });
        var ok = await _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = okRef });
        _output.WriteLine(ok);

        await WaitForText(_fixture.WinFormsHandle, "Modal result: OK name=Ada");
    }

    [Fact]
    public async Task WinForms_ModelessDialog_IsReportedButNotModal()
    {
        var buttonRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Dialogs", "Open Modeless Dialog");

        var result = await _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = buttonRef });
        _output.WriteLine(result);
        var dialog = ExtractHandle(result, "Test Modeless Dialog");
        Assert.DoesNotContain("[modal]", result);
        Assert.DoesNotContain("pending", result);

        var list = await _fixture.CallTool(new DialogsTool(_fixture.Session, _fixture.Dialogs, _fixture.Pending), new { });
        _output.WriteLine(list);
        Assert.Contains(dialog, list);

        await _fixture.CallTool(new NativeDialogTool(_fixture.Session), new { handle = dialog, action = "close" });
    }

    [Fact]
    public async Task Wpf_Dialog_AutoClick_ThenDriveDialogWithUia()
    {
        var buttonRef = await NavigateToTabAndFind(_fixture.WpfHandle, "Dialogs", "Open WPF Dialog");

        var (result, elapsed) = await Timed(() => _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = buttonRef }));
        _output.WriteLine($"click ({elapsed.TotalMilliseconds:0}ms): {result}");
        Assert.True(elapsed < Prompt, $"Click took {elapsed}");
        var dialog = ExtractHandle(result, "WPF Test Dialog");
        Assert.Contains("[modal]", result);

        var snapshot = await _fixture.CallTool(new SnapshotTool(_fixture.Session, _fixture.Elements, _fixture.Pending), new { handle = dialog });
        _output.WriteLine(snapshot);
        var inputRef = TestAppFixture.FindRefInSnapshot(snapshot, "Your name");
        var okRef = TestAppFixture.FindRefInSnapshot(snapshot, "OK");
        Assert.NotNull(inputRef);
        Assert.NotNull(okRef);

        await _fixture.CallTool(new FillTool(_fixture.Elements), new { @ref = inputRef, value = "Grace" });
        await _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = okRef });

        await WaitForText(_fixture.WpfHandle, "WPF dialog result: OK name=Grace");
        await WaitForPendingToFinish();
    }

    [Fact]
    public async Task Wpf_MessageBox_PressYesViaWin32()
    {
        var buttonRef = await NavigateToTabAndFind(_fixture.WpfHandle, "Dialogs", "Show Message Box");

        var result = await _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = buttonRef });
        _output.WriteLine(result);
        var dialog = ExtractHandle(result, "Confirm Discard");
        Assert.Contains("[win32]", result);

        var press = await _fixture.CallTool(new NativeDialogTool(_fixture.Session), new { handle = dialog, action = "press", button = "Yes" });
        _output.WriteLine(press);

        await WaitForText(_fixture.WpfHandle, "MessageBox result: Yes");
    }

    [Fact]
    public async Task Batch_StopsWhenAClickOpensADialog()
    {
        var buttonRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Dialogs", "Show Message Box");

        var batch = new BatchTool(_fixture.Session, _fixture.Elements, _fixture.Clicks);
        var result = await _fixture.CallTool(batch, new
        {
            actions = new object[]
            {
                new { action = "click", @ref = buttonRef },
                new { action = "wait", ms = 50 },
            }
        });
        _output.WriteLine(result);

        Assert.Contains("Confirm Delete", result);
        Assert.Contains("Stopped after action 1", result);
        Assert.DoesNotContain("2. wait", result);
    }

    private static string ExtractHandle(string result, string title)
    {
        var match = Regex.Match(result, $@"(w\d+) ""{Regex.Escape(title)}""");
        Assert.True(match.Success, $"No dialog handle for \"{title}\" in: {result}");
        return match.Groups[1].Value;
    }

    private static async Task<(string Result, TimeSpan Elapsed)> Timed(Func<Task<string>> call)
    {
        var sw = Stopwatch.StartNew();
        var result = await call();
        return (result, sw.Elapsed);
    }

    private async Task<string> NavigateToTabAndFind(string windowHandle, string tabName, string elementName)
    {
        var tabRef = _fixture.FindRefByName(windowHandle, tabName);
        Assert.NotNull(tabRef);
        await _fixture.CallTool(_fixture.CreateClickTool(), new { @ref = tabRef });

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
            var found = _fixture.FindRefByName(windowHandle, elementName);
            if (found != null) return found;
        }

        Assert.Fail($"Element \"{elementName}\" not found after navigating to \"{tabName}\" tab.");
        return "";
    }

    private async Task WaitForText(string windowHandle, string text)
    {
        var sw = Stopwatch.StartNew();
        var snapshot = "";
        while (sw.ElapsedMilliseconds < 5000)
        {
            snapshot = _fixture.TakeSnapshot(windowHandle);
            if (snapshot.Contains(text)) return;
            await Task.Delay(100);
        }
        _output.WriteLine(snapshot);
        Assert.Fail($"\"{text}\" did not appear.");
    }

    private async Task WaitForPendingToFinish()
    {
        var sw = Stopwatch.StartNew();
        while (_fixture.Pending.Running().Count > 0 && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
        }
        Assert.Empty(_fixture.Pending.Running());
        _fixture.Pending.TakeFinished();
    }
}
