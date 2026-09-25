using PlaywrightWindows.Mcp.Core.Snapshots;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class SnapshotTextTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string[] Rendered(CompactResult result) =>
        SnapshotText.Render(result.Lines).TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Parse_ReadsDepthRefAndNotes()
    {
        var parsed = ParsedSnapshot.Parse(Lines(
            "- window \"App\" [ref=w1e1]",
            "  - button \"OK\" [ref=w1e2] [disabled]",
            "... (snapshot truncated after 2 elements: limit of 2 elements. Call windows_snapshot ...)"));

        Assert.Equal(2, parsed.Lines.Count);
        Assert.Equal(new SnapshotLine(1, "button \"OK\" [ref=w1e2] [disabled]", "w1e2"), parsed.Lines[1]);
        Assert.True(parsed.Lines[1].HasState("disabled"));
        Assert.Single(parsed.Notes);
        Assert.Equal(new[] { -1, 0 }, parsed.Parents());
    }

    [Fact]
    public void HasState_IsNotFooledByTheName()
    {
        var line = ParsedSnapshot.Parse("- text \"see [offscreen] docs\" [ref=w1e4]").Lines[0];
        Assert.False(line.IsOffscreen);
    }

    [Fact]
    public void Compact_HidesOffscreenSubtrees_AndFlattensWrapperGroups()
    {
        var parsed = ParsedSnapshot.Parse(Lines(
            "- window \"App\" [ref=w1e1]",
            "  - group [ref=w1e2]",                         // wraps one element: flattened
            "    - group [ref=w1e3]",                       // and so does this one
            "      - button \"Save\" [ref=w1e4]",
            "  - list \"Files\" [ref=w1e5]",
            "    - listitem \"a.txt\" [ref=w1e6]",
            "    - listitem \"z.txt\" [ref=w1e7] [offscreen]",
            "      - text \"z\" [ref=w1e8] [offscreen]",
            "  - group [ref=w1e9]",                         // empty after hiding: dropped
            "    - button \"Hidden\" [ref=w1e10] [offscreen]",
            "  - group \"Options\" [ref=w1e11]",            // named: kept
            "    - checkbox \"Wrap\" [ref=w1e12]",
            "  - group [ref=w1e13]",                        // two elements: kept
            "    - button \"A\" [ref=w1e14]",
            "    - button \"B\" [ref=w1e15]"));

        var result = SnapshotText.Compact(parsed);

        Assert.Equal(new[]
        {
            "- window \"App\" [ref=w1e1]",
            "  - button \"Save\" [ref=w1e4]",
            "  - list \"Files\" [ref=w1e5]",
            "    - listitem \"a.txt\" [ref=w1e6]",
            "  - group \"Options\" [ref=w1e11]",
            "    - checkbox \"Wrap\" [ref=w1e12]",
            "  - group [ref=w1e13]",
            "    - button \"A\" [ref=w1e14]",
            "    - button \"B\" [ref=w1e15]",
        }, Rendered(result));
        Assert.Equal(3, result.HiddenOffscreen);
        Assert.Equal(3, result.FlattenedGroups);
    }

    [Fact]
    public void Compact_KeepsTheRoot()
    {
        var result = SnapshotText.Compact(ParsedSnapshot.Parse(Lines(
            "- group [ref=w1e1] [offscreen]",
            "  - button \"OK\" [ref=w1e2]")));
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public void CompactText_NotesHiddenElementsAndKeepsTruncationNote()
    {
        var text = SnapshotText.CompactText(Lines(
            "- window \"App\" [ref=w1e1]",
            "  - button \"Off\" [ref=w1e2] [offscreen]",
            "... (snapshot truncated after 2 elements: time limit. Call windows_snapshot ...)"));

        Assert.Contains("- window \"App\" [ref=w1e1]", text);
        Assert.DoesNotContain("Off", text);
        Assert.Contains("snapshot truncated after 2 elements: time limit", text);
        Assert.Contains("(1 offscreen elements hidden", text);
    }

    [Fact]
    public void Trim_StopsAtNodesOrChars()
    {
        var lines = SnapshotText.Compact(ParsedSnapshot.Parse(Lines(
            "- window \"App\" [ref=w1e1]",
            "  - button \"A\" [ref=w1e2]",
            "  - button \"B\" [ref=w1e3]"))).Lines;

        var byNodes = SnapshotText.Trim(lines, 2, int.MaxValue, out var truncated);
        Assert.True(truncated);
        Assert.Contains("button \"A\"", byNodes);
        Assert.DoesNotContain("button \"B\"", byNodes);
        Assert.Contains("truncated after 2 elements: limit of 2 elements", byNodes);

        var byChars = SnapshotText.Trim(lines, 100, 40, out truncated);
        Assert.True(truncated);
        Assert.Contains("limit of 40 characters", byChars);

        SnapshotText.Trim(lines, 100, 10_000, out truncated);
        Assert.False(truncated);
    }
}

public class SnapshotDiffTests
{
    private static ParsedSnapshot P(params string[] lines) => ParsedSnapshot.Parse(string.Join("\n", lines));

    private static readonly ParsedSnapshot Before = P(
        "- window \"App\" [ref=w1e1]",
        "  - group [ref=w1e2]",
        "    - button \"Save\" [ref=w1e3]",
        "    - checkbox \"Wrap\" [ref=w1e4]",
        "  - group \"Progress\" [ref=w1e5]",
        "    - text \"Loading...\" [ref=w1e6]",
        "    - progressbar [ref=w1e7]",
        "  - list \"Files\" [ref=w1e8]",
        "    - listitem \"a.txt\" [ref=w1e9]");

    [Fact]
    public void Identical_IsEmpty()
    {
        var diff = SnapshotDiff.Compute(Before, Before);
        Assert.True(diff.IsEmpty);
        Assert.Equal(9, diff.Unchanged);
        Assert.Equal("", diff.Render());
        Assert.Equal("no changes", diff.Summary);
    }

    [Fact]
    public void ReportsAddedSubtreesRemovedSubtreesAndChanges()
    {
        var after = P(
            "- window \"App\" [ref=w1e1]",
            "  - group [ref=w1e2]",
            "    - button \"Save\" [ref=w1e3] [disabled]",
            "    - checkbox \"Wrap\" [ref=w1e4]",
            "    - group \"Result\" [ref=w1e10]",
            "      - text \"Done\" [ref=w1e11]",
            "  - list \"Files\" [ref=w1e8]",
            "    - listitem \"a.txt\" [ref=w1e9] [offscreen]",
            "    - listitem \"b.txt\" [ref=w1e12] [offscreen]");

        var diff = SnapshotDiff.Compute(Before, after);

        Assert.Equal("2 added, 1 removed, 1 changed", diff.Summary);
        Assert.Equal(1, diff.HiddenOffscreenAdded);
        Assert.Equal(new[]
        {
            "added:",
            "  in window \"App\" [ref=w1e1]:",   // the unnamed wrapper group is skipped as context
            "    - group \"Result\" [ref=w1e10]",
            "      - text \"Done\" [ref=w1e11]",
            "removed:",
            "  - group \"Progress\" [ref=w1e5] (and 2 inside it)",
            "changed:",
            "  - button \"Save\" [ref=w1e3] [disabled]  (was: button \"Save\" [ref=w1e3])",
            "(1 new offscreen elements not shown)",
        }, diff.Render().TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')));
    }

    [Fact]
    public void RenderRespectsMaxChars()
    {
        var after = P(
            "- window \"App\" [ref=w1e1]",
            "  - button \"One\" [ref=w1e20]",
            "  - button \"Two\" [ref=w1e21]",
            "  - button \"Three\" [ref=w1e22]");

        var text = SnapshotDiff.Compute(P("- window \"App\" [ref=w1e1]"), after).Render(maxChars: 70);

        Assert.Contains("One", text);
        Assert.DoesNotContain("Three", text);
        Assert.Contains("more changes not shown", text);
    }
}
