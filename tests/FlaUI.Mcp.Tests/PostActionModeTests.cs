using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class PostActionModeTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("changes", PostActionMode.Changes)]
    [InlineData("FULL", PostActionMode.Full)]
    [InlineData("off", PostActionMode.Off)]
    [InlineData("0", PostActionMode.Off)]
    [InlineData("1", PostActionMode.Changes)]
    public void Parse(string value, PostActionMode mode)
    {
        Assert.Equal(mode, PostActionModes.Parse(value));
    }

    [Fact]
    public void Parse_Unknown_IsNull()
    {
        Assert.Null(PostActionModes.Parse("sometimes"));
        Assert.Null(PostActionModes.Parse(null));
    }

    [Fact]
    public void FromArgument_KeepsTheOldBooleanMeaning()
    {
        Assert.Equal(PostActionMode.Changes, PostActionModes.FromArgument(Args("""{"ref":"w1e2"}"""), PostActionMode.Changes));
        Assert.Equal(PostActionMode.Off, PostActionModes.FromArgument(Args("""{"postSnapshot":false}"""), PostActionMode.Changes));
        Assert.Equal(PostActionMode.Full, PostActionModes.FromArgument(Args("""{"postSnapshot":true}"""), PostActionMode.Full));
        // Asking for it when the server default is off turns it on.
        Assert.Equal(PostActionMode.Changes, PostActionModes.FromArgument(Args("""{"postSnapshot":true}"""), PostActionMode.Off));
        Assert.Equal(PostActionMode.Full, PostActionModes.FromArgument(Args("""{"postSnapshot":"full"}"""), PostActionMode.Changes));
        Assert.Equal(PostActionMode.Changes, PostActionModes.FromArgument(Args("""{"postSnapshot":"nonsense"}"""), PostActionMode.Changes));
        Assert.Equal(PostActionMode.Changes, PostActionModes.FromArgument(null, PostActionMode.Changes));
    }

    [Fact]
    public void Changes_NeverDumpsATreeForAWindowItHasNotSeen()
    {
        Assert.Equal(PostActionView.Summary,
            PostActionModes.Choose(PostActionMode.Changes, PostActionTargetKind.Foreground, comparable: false, 0, 8000));
        Assert.Equal(PostActionView.Summary,
            PostActionModes.Choose(PostActionMode.Changes, PostActionTargetKind.SourceWindow, comparable: false, 0, 10));
    }

    [Fact]
    public void Changes_ShowsANewDialog_AndChangesWheneverItCanCompare()
    {
        Assert.Equal(PostActionView.Snapshot,
            PostActionModes.Choose(PostActionMode.Changes, PostActionTargetKind.NewDialog, comparable: false, 0, 900));
        // Even a change list longer than a snapshot: changes are what was asked for.
        Assert.Equal(PostActionView.Changes,
            PostActionModes.Choose(PostActionMode.Changes, PostActionTargetKind.Foreground, comparable: true, 5000, 3000));
    }

    [Fact]
    public void Full_IsTheEarlierBehaviour()
    {
        Assert.Equal(PostActionView.Snapshot,
            PostActionModes.Choose(PostActionMode.Full, PostActionTargetKind.Foreground, comparable: false, 0, 8000));
        Assert.Equal(PostActionView.Changes,
            PostActionModes.Choose(PostActionMode.Full, PostActionTargetKind.Foreground, comparable: true, 300, 8000));
        Assert.Equal(PostActionView.Snapshot,
            PostActionModes.Choose(PostActionMode.Full, PostActionTargetKind.Foreground, comparable: true, 9000, 8000));
    }
}

public class ScreenshotOutputTests
{
    [Theory]
    [InlineData("file", ScreenshotOutput.File)]
    [InlineData("Preview", ScreenshotOutput.Preview)]
    [InlineData("image", ScreenshotOutput.Image)]
    [InlineData("path", ScreenshotOutput.File)]
    public void Parse(string value, ScreenshotOutput output)
    {
        Assert.Equal(output, ScreenshotOutputs.Parse(value));
    }

    [Fact]
    public void Parse_Unknown_IsNull()
    {
        Assert.Null(ScreenshotOutputs.Parse("gif"));
    }

    [Fact]
    public void DefaultPath_IsTimestampedUnderTemp_WithASafeLabel()
    {
        var path = ScreenshotOutputs.DefaultPath("/tmp", new DateTime(2026, 9, 25, 14, 3, 7, 42), "w1e5/..\\x");
        Assert.Equal(Path.Combine("/tmp", "flaui-mcp", "screenshot-20260925-140307-042-w1e5x.png"), path);
        Assert.EndsWith("screenshot-20260925-140307-042.png", ScreenshotOutputs.DefaultPath("/tmp", new DateTime(2026, 9, 25, 14, 3, 7, 42)));
    }

    [Theory]
    [InlineData(1920, 1080, 800, 450)]
    [InlineData(1080, 1920, 450, 800)]
    [InlineData(640, 480, 640, 480)]
    [InlineData(3000, 10, 800, 3)]
    public void PreviewSize_FitsAndNeverEnlarges(int w, int h, int ew, int eh)
    {
        Assert.Equal((ew, eh), ScreenshotOutputs.PreviewSize(w, h));
    }
}
