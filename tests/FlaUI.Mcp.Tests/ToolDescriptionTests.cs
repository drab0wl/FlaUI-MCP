using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>Keeps the server instructions and tool descriptions in step with the tools.</summary>
public class ToolDescriptionTests
{
    /// <summary>Every tool, read without constructing it (Name and Description are constants).</summary>
    private static readonly IReadOnlyList<ToolBase> Tools = typeof(ToolBase).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(ToolBase)) && !t.IsAbstract)
        .Select(t => (ToolBase)RuntimeHelpers.GetUninitializedObject(t))
        .ToList();

    [Fact]
    public void InstructionsOnlyNameToolsThatExist()
    {
        var names = Tools.Select(t => t.Name).ToHashSet();
        var mentioned = Regex.Matches(ServerInstructions.Default, @"windows_\w+").Select(m => m.Value).Distinct();

        Assert.All(mentioned, name => Assert.Contains(name, names));
    }

    [Fact]
    public void DescriptionsNameOnlyToolsThatExist_AndStayShort()
    {
        var names = Tools.Select(t => t.Name).ToHashSet();
        Assert.All(Tools, tool =>
        {
            Assert.True(tool.Description.Length <= 700, $"{tool.Name}: {tool.Description.Length} chars");
            foreach (Match m in Regex.Matches(tool.Description, @"windows_\w+"))
            {
                Assert.Contains(m.Value, names);
            }
        });
    }

    [Fact]
    public void ToolNamesAreUnique()
    {
        Assert.Equal(Tools.Count, Tools.Select(t => t.Name).Distinct().Count());
    }
}
