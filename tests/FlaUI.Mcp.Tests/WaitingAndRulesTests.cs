using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Batch;
using PlaywrightWindows.Mcp.Core.Waiting;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class DialogRulesTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_ListOrSingleRule()
    {
        var rules = DialogRules.Parse(J("""{"onDialog":[{"titleContains":"Save changes","press":"no"},{"textContains":"overwrite","press":"yes"}]}"""), out var error);
        Assert.Null(error);
        Assert.Equal(2, rules.Count);
        Assert.Equal(new DialogRule(null, "Save changes", null, "no"), rules[0]);

        Assert.Single(DialogRules.Parse(J("""{"onDialog":{"title":"Confirm","press":"ok"}}"""), out _));
        Assert.Empty(DialogRules.Parse(J("""{"actions":[]}"""), out error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("""{"onDialog":[{"titleContains":"x"}]}""", "press")]
    [InlineData("""{"onDialog":[{"press":"ok"}]}""", "title")]
    [InlineData("""{"onDialog":"yes"}""", "list")]
    public void Parse_RejectsRulesThatAreIncompleteOrTooBroad(string json, string mentions)
    {
        Assert.Empty(DialogRules.Parse(J(json), out var error));
        Assert.Contains(mentions, error);
    }

    [Fact]
    public void Match_NeedsEveryCondition()
    {
        var rules = new[]
        {
            new DialogRule("Confirm Delete", null, "selected item", "no"),
            new DialogRule(null, "save", null, "cancel"),
        };
        Assert.Equal("no", DialogRules.Match(rules, "&Confirm Delete", "Delete the selected item?")!.Press);
        Assert.Null(DialogRules.Match(rules.Take(1).ToList(), "Confirm Delete", "Delete everything?"));
        Assert.Equal("cancel", DialogRules.Match(rules, "Save Changes?", "")!.Press);
        Assert.Null(DialogRules.Match(rules, "Error", "Oops"));
        Assert.Contains("title*=\"save\" -> press \"cancel\"", rules[1].Describe());
    }
}

public class WaitingTests
{
    [Fact]
    public async Task UntilAsync_WakesOnSignalNotOnTheFallback()
    {
        var signal = new ChangeSignal();
        var done = false;
        _ = Task.Run(async () => { await Task.Delay(100); done = true; signal.Pulse(); });

        var sw = Stopwatch.StartNew();
        var met = await Poll.UntilAsync(() => done, TimeSpan.FromSeconds(5), signal, fallback: TimeSpan.FromSeconds(3));

        Assert.True(met);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task UntilAsync_FallsBackToPolling_AndTimesOut()
    {
        var calls = 0;
        var sw = Stopwatch.StartNew();
        var met = await Poll.UntilAsync(() => ++calls < 0, TimeSpan.FromMilliseconds(300), null, TimeSpan.FromMilliseconds(50));

        Assert.False(met);
        Assert.InRange(calls, 3, 10);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ChangeSignal_TracksQuietTime_AndCoalescesPulses()
    {
        var signal = new ChangeSignal();
        signal.Pulse();
        signal.Pulse();
        Assert.True(signal.Quiet < TimeSpan.FromSeconds(1));
        Assert.True(await signal.WaitAsync(TimeSpan.Zero));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero)); // both pulses became one wake-up
    }

    [Fact]
    public void Idle_Rules()
    {
        var stable = TimeSpan.FromMilliseconds(500);
        Assert.True(Idle.IsIdle(true, false, TimeSpan.FromSeconds(1), stable));
        Assert.False(Idle.IsIdle(false, false, TimeSpan.FromSeconds(1), stable));
        Assert.False(Idle.IsIdle(true, true, TimeSpan.FromSeconds(1), stable));
        Assert.False(Idle.IsIdle(true, false, TimeSpan.FromMilliseconds(100), stable));

        Assert.True(Idle.IsRunning(40, 0, 100));
        Assert.False(Idle.IsRunning(0, 0, 100));
        Assert.False(Idle.IsRunning(100, 0, 100));
        Assert.False(Idle.IsRunning(0, 0, 0));

        Assert.Equal("a progress bar is running, the UI changed 100ms ago",
            Idle.Describe(true, true, TimeSpan.FromMilliseconds(100), stable));
        Assert.Equal("idle", Idle.Describe(true, false, TimeSpan.FromSeconds(2), stable));
    }

    [Fact]
    public void AnnotationLayout_ClipsToTheCapture_AndPlacesLabels()
    {
        var capture = new Rectangle(100, 100, 400, 300);
        var boxes = AnnotationLayout.Place(new[]
        {
            ("w1e1", new Rectangle(150, 150, 50, 20)),   // inside, label above
            ("w1e2", new Rectangle(110, 102, 40, 20)),   // at the top edge, label inside
            ("w1e3", new Rectangle(480, 380, 100, 100)), // partly outside: clipped
            ("w1e4", new Rectangle(900, 900, 10, 10)),   // outside: dropped
            ("w1e5", new Rectangle(200, 200, 2, 2)),     // too small: dropped
        }, capture);

        Assert.Equal(new[] { "w1e1", "w1e2", "w1e3" }, boxes.Select(b => b.Ref));
        Assert.Equal(new Rectangle(50, 50, 50, 20), boxes[0].Box);
        Assert.Equal(new Point(50, 50 - AnnotationLayout.LabelHeight), boxes[0].Label);
        Assert.Equal(new Point(10, 2), boxes[1].Label);
        Assert.Equal(new Rectangle(380, 280, 20, 20), boxes[2].Box);
    }
}
