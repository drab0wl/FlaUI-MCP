using System.Text.Json;
using PlaywrightWindows.Mcp;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class ServerInstructionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("C:\\no\\such\\file.md")]
    public void DefaultsToTheBuiltInText(string? setting)
    {
        Assert.Equal(ServerInstructions.Default, ServerInstructions.Resolve(setting, _ => false, _ => ""));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("0")]
    [InlineData("none")]
    public void CanBeTurnedOff(string setting)
    {
        Assert.Null(ServerInstructions.Resolve(setting, _ => false, _ => ""));
    }

    [Fact]
    public void CanComeFromAFile()
    {
        Assert.Equal("Only use windows_find.", ServerInstructions.Resolve("my.md", p => p == "my.md", _ => "  Only use windows_find.\n"));
        Assert.Null(ServerInstructions.Resolve("empty.md", _ => true, _ => "  \n"));
    }

    [Fact]
    public void StaysShort()
    {
        // It goes into every conversation's system prompt.
        Assert.True(ServerInstructions.Default.Length < 1500, $"{ServerInstructions.Default.Length} chars");
    }

    [Fact]
    public void InitializeResult_IncludesInstructionsOnlyWhenSet()
    {
        var with = JsonSerializer.Serialize(new McpInitializeResult { Instructions = "hi" }, McpProtocol.JsonOptions);
        var without = JsonSerializer.Serialize(new McpInitializeResult(), McpProtocol.JsonOptions);

        Assert.Contains("\"instructions\":\"hi\"", with);
        Assert.DoesNotContain("instructions", without);
    }
}
