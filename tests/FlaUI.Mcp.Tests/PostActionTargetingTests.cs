using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Dialogs;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class PostActionTargetingTests
{
    private const int Pid = 7;
    private const nint Main = 0x10;
    private static readonly DialogInfo Outer = new(0x20, Pid, Main, "#32770", "Save changes?", true, false);
    private static readonly DialogInfo Inner = new(0x30, Pid, 0x20, "#32770", "Are you sure?", true, true);
    private static readonly DialogInfo OtherApp = new(0x40, 99, 0x50, "#32770", "Other", true, true);

    private static bool AllOpen(nint _) => true;

    [Fact]
    public void NewDialog_WinsOverForeground_AndPrefersTheEnabledOne()
    {
        var target = PostActionTargeting.Choose(Pid, new[] { Outer, Inner }, Main, Pid, Array.Empty<DialogInfo>(), Main, AllOpen);
        Assert.Equal(new PostActionTarget(Inner.Hwnd, PostActionTargetKind.NewDialog), target);
    }

    [Fact]
    public void NewDialogThatAlreadyClosed_IsIgnored()
    {
        var target = PostActionTargeting.Choose(Pid, new[] { Inner }, Main, Pid, Array.Empty<DialogInfo>(), Main, h => h != Inner.Hwnd);
        Assert.Equal(new PostActionTarget(Main, PostActionTargetKind.Foreground), target);
    }

    [Fact]
    public void ForegroundOfAnotherApp_FallsBackToOpenDialogThenSourceWindow()
    {
        var withDialog = PostActionTargeting.Choose(Pid, Array.Empty<DialogInfo>(), 0x99, 42, new[] { OtherApp, Inner }, Main, AllOpen);
        Assert.Equal(new PostActionTarget(Inner.Hwnd, PostActionTargetKind.OpenDialog), withDialog);

        var withoutDialog = PostActionTargeting.Choose(Pid, Array.Empty<DialogInfo>(), 0x99, 42, new[] { OtherApp }, Main, AllOpen);
        Assert.Equal(new PostActionTarget(Main, PostActionTargetKind.SourceWindow), withoutDialog);
    }

    [Fact]
    public void NothingKnown_IsNone()
    {
        var target = PostActionTargeting.Choose(0, Array.Empty<DialogInfo>(), 0x99, 42, Array.Empty<DialogInfo>(), 0, AllOpen);
        Assert.Equal(PostActionTargetKind.None, target.Kind);
    }

    [Fact]
    public void Header_NamesWhatIsShown()
    {
        Assert.Equal("--- after click: dialog it opened w5 \"Confirm\" ---",
            PostActionTargeting.Header("click", "w5", "Confirm", PostActionTargetKind.NewDialog));
        Assert.Equal("--- after fill: foreground window w1 \"App\" ---",
            PostActionTargeting.Header("fill", "w1", "App", PostActionTargetKind.Foreground));
    }
}
