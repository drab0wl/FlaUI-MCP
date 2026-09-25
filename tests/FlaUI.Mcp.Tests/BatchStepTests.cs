using System.Text.Json;
using PlaywrightWindows.Mcp.Core.Batch;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class BatchStepTests
{
    private static BatchStep Step(string json) => BatchStep.Parse(JsonDocument.Parse(json).RootElement);

    private static List<BatchStep> Steps(params string[] json) => json.Select(Step).ToList();

    [Fact]
    public void Parse_RefStep_HasNoSelector()
    {
        var step = Step("""{"action":"Click","ref":"w1e5","mode":"input"}""");
        Assert.Equal("click", step.Action);
        Assert.Equal("w1e5", step.Ref);
        Assert.Equal("input", step.Mode);
        Assert.Null(step.Selector);
        Assert.Null(step.SelectorError);
    }

    [Fact]
    public void Parse_NestedSelector()
    {
        var step = Step("""{"action":"click","selector":{"name":"OK","role":"button","handle":"w3","index":1}}""");
        Assert.Equal(new ElementSelector(Name: "OK", Role: "button", Handle: "w3", Index: 1), step.Selector);
    }

    [Fact]
    public void Parse_FlatSelectorFields()
    {
        var step = Step("""{"action":"fill","automationId":"txtName","handle":"$dialog","value":"Ada"}""");
        Assert.Equal(new ElementSelector(AutomationId: "txtName", Handle: "$dialog"), step.Selector);
        Assert.Equal("Ada", step.Value);
    }

    [Fact]
    public void Parse_NestedSelectorTakesHandleFromStep()
    {
        var step = Step("""{"action":"click","handle":"w4","selector":{"nameContains":"Sav"}}""");
        Assert.Equal("w4", step.Selector!.Handle);
    }

    [Fact]
    public void Parse_BracketedNameIsAnAutomationId()
    {
        // Snapshots print unnamed elements as [automationId].
        var step = Step("""{"action":"click","name":"[btnSave]"}""");
        Assert.Equal(new ElementSelector(AutomationId: "btnSave"), step.Selector);
    }

    [Fact]
    public void Parse_UnknownRole_IsAnError()
    {
        var step = Step("""{"action":"click","role":"banana"}""");
        Assert.Null(step.Selector);
        Assert.Contains("Unknown role", step.SelectorError);
    }

    [Fact]
    public void Parse_EmptyNestedSelector_IsAnError()
    {
        Assert.NotNull(Step("""{"action":"click","selector":{"handle":"w1"}}""").SelectorError);
    }

    [Fact]
    public void Parse_SnapshotHandleIsNotASelector()
    {
        var step = Step("""{"action":"snapshot","handle":"w2"}""");
        Assert.Null(step.Selector);
        Assert.Equal("w2", step.Handle);
    }

    [Theory]
    [InlineData("dialog_open", "dialog_open")]
    [InlineData("Dialog_Opened", "dialog_open")]
    [InlineData("element_appears", "element")]
    [InlineData("element_text_contains", "text_contains")]
    [InlineData("gone", "element_gone")]
    [InlineData("later", "?later")]
    public void Parse_UntilAliases(string until, string expected)
    {
        Assert.Equal(expected, Step($$"""{"action":"wait","until":"{{until}}"}""").Until);
    }

    [Fact]
    public void Describe_Selector()
    {
        Assert.Equal("button \"OK\" #btnOk [2] in $dialog",
            new ElementSelector("OK", null, "btnOk", "button", "$dialog", 2).Describe());
    }

    [Fact]
    public void ExpectedDialog_DoesNotStopTheBatch()
    {
        var steps = Steps(
            """{"action":"click","ref":"w1e2"}""",
            """{"action":"wait","until":"dialog_open"}""",
            """{"action":"dialog_press","button":"yes"}""");

        Assert.True(BatchPlan.ExpectsDialog(steps, 0));
        Assert.False(BatchPlan.StopAfterClick(dialogOpened: true, stillRunning: true, steps, 0));
        // And there's no point waiting for the dialog to settle: the wait does that.
        Assert.Equal(TimeSpan.Zero, BatchPlan.SettleTime(steps, 0));
    }

    [Fact]
    public void UnexpectedDialog_StopsTheBatch()
    {
        var steps = Steps(
            """{"action":"click","ref":"w1e2"}""",
            """{"action":"click","ref":"w1e3"}""");

        Assert.True(BatchPlan.StopAfterClick(dialogOpened: true, stillRunning: false, steps, 0));
        Assert.True(BatchPlan.StopAfterClick(dialogOpened: false, stillRunning: true, steps, 0));
        Assert.False(BatchPlan.StopAfterClick(dialogOpened: false, stillRunning: false, steps, 0));
        // Last step: nothing expects a dialog either.
        Assert.True(BatchPlan.StopAfterClick(dialogOpened: true, stillRunning: false, steps, 1));
    }

    [Fact]
    public void WaitForOtherThings_DoesNotExpectADialog()
    {
        var steps = Steps(
            """{"action":"click","ref":"w1e2"}""",
            """{"action":"wait","until":"element","name":"Done"}""");
        Assert.False(BatchPlan.ExpectsDialog(steps, 0));
        Assert.Null(BatchPlan.SettleTime(steps, 0));
    }

    [Fact]
    public void SettleTime_NoDialogAndExplicitSettle()
    {
        var steps = Steps(
            """{"action":"click","ref":"w1e2","noDialog":true}""",
            """{"action":"click","ref":"w1e3","settleMs":80}""",
            """{"action":"click","ref":"w1e4","settleMs":999999,"noDialog":true}""");

        Assert.Equal(TimeSpan.Zero, BatchPlan.SettleTime(steps, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(80), BatchPlan.SettleTime(steps, 1));
        Assert.Equal(TimeSpan.FromSeconds(5), BatchPlan.SettleTime(steps, 2));
    }

    [Fact]
    public void WaitTimeout_DefaultsAndClampsToBudget()
    {
        var step = Step("""{"action":"wait","until":"dialog_open"}""");
        Assert.Equal(BatchPlan.DefaultWaitTimeout, BatchPlan.WaitTimeout(step, TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(2), BatchPlan.WaitTimeout(step, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.Zero, BatchPlan.WaitTimeout(step, TimeSpan.FromSeconds(-1)));

        var custom = Step("""{"action":"wait","until":"element","timeoutMs":12000}""");
        Assert.Equal(TimeSpan.FromSeconds(12), BatchPlan.WaitTimeout(custom, TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [InlineData("Saved 3 files", "saved", true)]
    [InlineData("Saved 3 files", "4 files", false)]
    [InlineData(null, "x", false)]
    public void TextMatches_CaseInsensitiveSubstring(string? actual, string expected, bool match)
    {
        Assert.Equal(match, BatchPlan.TextMatches(actual, expected));
    }

    [Fact]
    public void Parse_Keys()
    {
        Assert.Equal(new[] { "Ctrl+Shift+B" }, Step("""{"action":"keys","keys":["Ctrl+Shift+B"]}""").Keys);
        Assert.Equal(new[] { "Down", "Enter" }, Step("""{"action":"keys","keys":["Down","Enter"]}""").Keys);
        Assert.Equal(new[] { "F5" }, Step("""{"action":"keys","keys":"F5"}""").Keys);
        Assert.Equal(new[] { "Alt+F4" }, Step("""{"action":"keys","chord":"Alt+F4"}""").Keys);
        Assert.Null(Step("""{"action":"click","ref":"w1e1"}""").Keys);
    }

    [Fact]
    public void KeysFollowedByDialogWait_SkipSettle()
    {
        var steps = Steps(
            """{"action":"keys","keys":["Ctrl+S"]}""",
            """{"action":"wait","until":"dialog_open"}""");
        Assert.True(BatchPlan.ExpectsDialog(steps, 0));
        Assert.Equal(TimeSpan.Zero, BatchPlan.SettleTime(steps, 0));
    }

    [Fact]
    public void Parse_Compact()
    {
        Assert.True(Step("""{"action":"snapshot","compact":true}""").Compact);
        Assert.Null(Step("""{"action":"snapshot"}""").Compact);
    }

    [Fact]
    public void FindFormat_GroupsByWindowWithPaths()
    {
        var text = FindFormat.Render(new List<(string, string, IReadOnlyList<string>)>
        {
            ("w1", "button \"Build\" [ref=w1e40]", new[] { "toolbar \"Standard\"" }),
            ("w1", "menuitem \"Build\" [ref=w1e7]", Array.Empty<string>()),
            ("w5", "button \"Build\" [ref=w5e2]", new[] { "group \"Options\"", "tab \"General\"" }),
        }, more: true, limit: 3);

        Assert.Equal(new[]
        {
            "in w1:",
            "- button \"Build\" [ref=w1e40]  (in toolbar \"Standard\")",
            "- menuitem \"Build\" [ref=w1e7]",
            "in w5:",
            "- button \"Build\" [ref=w5e2]  (in group \"Options\" > tab \"General\")",
            "... more than 3 matches; narrow the selector (role, handle) or raise limit.",
        }, text.Split('\n').Select(l => l.TrimEnd('\r')));
    }

    [Fact]
    public void Parse_StateAndMenuSteps()
    {
        Assert.Equal("Blue", Step("""{"action":"select","name":"Color","option":"Blue"}""").Option);
        Assert.Equal("75", Step("""{"action":"set_value","name":"Volume","value":75}""").Value);
        Assert.Equal("0.5", Step("""{"action":"set_value","name":"Volume","value":"0.5"}""").Value);
        Assert.Equal(new[] { "File", "Save As..." }, Step("""{"action":"menu","path":"File > Save As..."}""").Path);
        Assert.Null(Step("""{"action":"menu","path":"File > Open"}""").Selector);
    }

    [Fact]
    public void MenuFollowedByDialogWait_IsExpected()
    {
        var steps = Steps(
            """{"action":"menu","path":["File","Open..."]}""",
            """{"action":"wait","until":"dialog_open"}""");
        Assert.True(BatchPlan.ExpectsDialog(steps, 0));
        Assert.False(BatchPlan.StopAfterClick(dialogOpened: true, stillRunning: true, steps, 0));
        Assert.Contains("menu", BatchPlan.ClickLike);
        Assert.Contains("check", BatchPlan.ClickLike);
    }
}
