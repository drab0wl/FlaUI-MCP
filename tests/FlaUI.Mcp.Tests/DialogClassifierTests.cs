using PlaywrightWindows.Mcp.Core.Dialogs;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class DialogClassifierTests
{
    private const int Pid = 100;
    private const nint Main = 0x10;

    private static TopLevelWindowInfo Win(
        nint hwnd,
        string cls = "HwndWrapper[App;;1]",
        string title = "",
        nint owner = 0,
        bool visible = true,
        bool enabled = true,
        bool ownerEnabled = true,
        int pid = Pid) =>
        new(hwnd, pid, owner, cls, title, visible, enabled, ownerEnabled);

    [Fact]
    public void MainWindow_IsNotADialog()
    {
        var dialogs = DialogClassifier.FindDialogs(new[] { Win(Main, title: "My App") });
        Assert.Empty(dialogs);
    }

    [Fact]
    public void MessageBox_OwnedByDisabledMainWindow_IsModalWin32Dialog()
    {
        var dialogs = DialogClassifier.FindDialogs(new[]
        {
            Win(Main, title: "My App", enabled: false),
            Win(0x20, cls: "#32770", title: "Confirm", owner: Main, ownerEnabled: false),
        });

        var d = Assert.Single(dialogs);
        Assert.Equal((nint)0x20, d.Hwnd);
        Assert.True(d.IsModal);
        Assert.True(d.IsWin32Dialog);
        Assert.True(d.IsBlocking);
    }

    [Fact]
    public void ModelessOwnedWindow_IsDialogButNotBlocking()
    {
        var dialogs = DialogClassifier.FindDialogs(new[]
        {
            Win(Main, title: "My App"),
            Win(0x30, cls: "WindowsForms10.Window.8.app.0.1", title: "Find", owner: Main, ownerEnabled: true),
        });

        var d = Assert.Single(dialogs);
        Assert.False(d.IsModal);
        Assert.False(d.IsBlocking);
    }

    [Fact]
    public void UntitledOwnedPopup_IsIgnored()
    {
        var dialogs = DialogClassifier.FindDialogs(new[]
        {
            Win(0x40, cls: "Popup", title: "", owner: Main, ownerEnabled: true),
        });
        Assert.Empty(dialogs);
    }

    [Fact]
    public void UntitledOwnedWindow_WithDisabledOwner_IsStillModal()
    {
        var dialogs = DialogClassifier.FindDialogs(new[]
        {
            Win(0x41, cls: "HwndWrapper[x]", title: "", owner: Main, ownerEnabled: false),
        });
        Assert.True(Assert.Single(dialogs).IsModal);
    }

    [Fact]
    public void HiddenAndNoiseWindows_AreIgnored()
    {
        var dialogs = DialogClassifier.FindDialogs(new[]
        {
            Win(0x50, cls: "#32770", title: "Hidden", owner: Main, ownerEnabled: false, visible: false),
            Win(0x51, cls: "tooltips_class32", title: "tip", owner: Main, ownerEnabled: false),
            Win(0x52, cls: "#32768", title: "", owner: Main, ownerEnabled: false),
        });
        Assert.Empty(dialogs);
    }

    [Fact]
    public void ProcessFilter_ExcludesOtherProcesses()
    {
        var dialogs = DialogClassifier.FindDialogs(
            new[]
            {
                Win(0x60, cls: "#32770", title: "Mine", owner: Main, ownerEnabled: false, pid: Pid),
                Win(0x61, cls: "#32770", title: "Theirs", owner: 0x99, ownerEnabled: false, pid: 999),
            },
            new HashSet<int> { Pid });

        Assert.Equal("Mine", Assert.Single(dialogs).Title);
    }

    [Fact]
    public void NestedModal_OuterIsReportedAsBlocked()
    {
        var dialogs = DialogClassifier.FindDialogs(new[]
        {
            Win(Main, title: "App", enabled: false),
            Win(0x70, cls: "#32770", title: "Options", owner: Main, ownerEnabled: false, enabled: false),
            Win(0x71, cls: "#32770", title: "Are you sure?", owner: 0x70, ownerEnabled: false),
        });

        Assert.Equal(2, dialogs.Count);
        Assert.False(dialogs.Single(d => d.Title == "Options").IsEnabled);
        Assert.True(dialogs.Single(d => d.Title == "Are you sure?").IsEnabled);
        Assert.Contains("[blocked by another dialog]", DialogClassifier.Describe(dialogs.Single(d => d.Title == "Options")));
    }

    [Fact]
    public void NewSince_ReturnsOnlyDialogsNotInBaseline()
    {
        var a = new DialogInfo(0x1, Pid, Main, "#32770", "A", true, true);
        var b = new DialogInfo(0x2, Pid, Main, "#32770", "B", true, true);

        var fresh = DialogClassifier.NewSince(new[] { a }, new[] { a, b });

        Assert.Equal("B", Assert.Single(fresh).Title);
    }
}
