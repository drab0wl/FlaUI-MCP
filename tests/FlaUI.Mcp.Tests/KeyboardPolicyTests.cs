using PlaywrightWindows.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class KeyboardPolicyTests
{
    private static readonly IReadOnlySet<int> Tracked = new HashSet<int> { 10, 20 };

    [Fact]
    public void WithATarget_ItsAppMustBeInFront()
    {
        Assert.Null(KeyboardPolicy.Decide(10, Tracked, 10));
        Assert.Equal(KeyboardPolicy.Refusal.NotTarget, KeyboardPolicy.Decide(20, Tracked, 10));
        Assert.Equal(KeyboardPolicy.Refusal.NotTarget, KeyboardPolicy.Decide(99, Tracked, 10));
    }

    [Fact]
    public void WithoutATarget_AnyAutomatedAppMayBeInFront()
    {
        Assert.Null(KeyboardPolicy.Decide(20, Tracked, null));
        Assert.Equal(KeyboardPolicy.Refusal.NotTracked, KeyboardPolicy.Decide(99, Tracked, null));
    }

    [Fact]
    public void NothingTrackedYet_Allows()
    {
        Assert.Null(KeyboardPolicy.Decide(99, new HashSet<int>(), null));
    }

    [Fact]
    public void Message_NamesTheWindowAndTheWayOut()
    {
        var text = KeyboardPolicy.Message(KeyboardPolicy.Refusal.NotTracked, "Slack", 99);
        Assert.Contains("\"Slack\" (pid 99)", text);
        Assert.Contains("windows_focus", text);
        Assert.Contains("FLAUI_MCP_KEYBOARD_GUARD=0", text);
    }

    [Theory]
    [InlineData("42", 42.0)]
    [InlineData(" 0.5 ", 0.5)]
    [InlineData("1e3", 1000.0)]
    public void ValueParsing_Numbers(string text, double expected)
    {
        Assert.True(ValueParsing.TryParseNumber(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ValueParsing_RejectsText()
    {
        Assert.False(ValueParsing.TryParseNumber("loud", out _));
    }
}
