using PlaywrightWindows.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class RefTableTests
{
    private static string[] Snapshot(RefTable<string> table, string window, params (string Item, string? Key)[] items)
    {
        return SnapshotTruncated(table, window, false, items);
    }

    private static string[] SnapshotTruncated(RefTable<string> table, string window, bool truncated, params (string Item, string? Key)[] items)
    {
        table.BeginSnapshot(window);
        var refs = items.Select(i => table.Register(window, i.Item, i.Key)).ToArray();
        table.CompleteSnapshot(window, truncated);
        return refs;
    }

    [Fact]
    public void FirstSnapshot_NumbersFromOne()
    {
        var table = new RefTable<string>();
        Assert.Equal(new[] { "w1e1", "w1e2", "w1e3" }, Snapshot(table, "w1", ("a", "1"), ("b", "2"), ("c", null)));
        Assert.Equal("b", table.Get("w1e2"));
    }

    [Fact]
    public void ResnapshotKeepsRefsForTheSameElements()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", "1"), ("b", "2"), ("c", "3"));

        // "x" appeared before "b"; "c" is gone.
        var refs = Snapshot(table, "w1", ("a2", "1"), ("x", "9"), ("b2", "2"));

        Assert.Equal(new[] { "w1e1", "w1e4", "w1e2" }, refs);
        Assert.Equal("b2", table.Get("w1e2"));
        Assert.Null(table.Get("w1e3"));
    }

    [Fact]
    public void WithoutStableKeys_RenumbersLikeBefore()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", null), ("b", null));
        Assert.Equal(new[] { "w1e1", "w1e2" }, Snapshot(table, "w1", ("c", null), ("d", null)));
        Assert.Equal("c", table.Get("w1e1"));
    }

    [Fact]
    public void TruncatedSnapshot_KeepsRefsItDidNotReach()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", "1"), ("b", "2"), ("c", "3"));

        SnapshotTruncated(table, "w1", true, ("a2", "1"));

        Assert.Equal("a2", table.Get("w1e1"));
        Assert.Equal("b", table.Get("w1e2"));
        Assert.Equal("c", table.Get("w1e3"));

        // And they stay stable in the next full snapshot.
        Assert.Equal(new[] { "w1e1", "w1e2", "w1e3" }, Snapshot(table, "w1", ("a", "1"), ("b", "2"), ("c", "3")));
    }

    [Fact]
    public void DuplicateKeyInOneSnapshot_GetsItsOwnRef()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", "1"));
        Assert.Equal(new[] { "w1e1", "w1e2" }, Snapshot(table, "w1", ("a", "1"), ("a-again", "1")));
    }

    [Fact]
    public void RegisterFound_ReusesTheSnapshotRef()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", "1"), ("b", "2"));

        Assert.Equal("w1e2", table.RegisterFound("w1", "b-found", "2"));
        Assert.Equal("b-found", table.Get("w1e2"));

        var fresh = table.RegisterFound("w1", "z", "7");
        Assert.Equal("w1e3", fresh);
        Assert.Equal(fresh, table.RegisterFound("w1", "z-again", "7"));

        // A found element keeps its ref in the next snapshot too.
        Assert.Equal(new[] { "w1e1", "w1e3" }, Snapshot(table, "w1", ("a", "1"), ("z", "7")));
    }

    [Fact]
    public void WindowsAreIndependent()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", "1"));
        Snapshot(table, "w2", ("b", "1"));
        Snapshot(table, "w1", ("a", "1"));

        Assert.Equal("a", table.Get("w1e1"));
        Assert.Equal("b", table.Get("w2e1"));
    }

    [Theory]
    [InlineData("w3e12", "w3")]
    [InlineData("w10e1", "w10")]
    [InlineData("c3", null)]
    [InlineData("w3", null)]
    public void WindowHandleOf(string refId, string? handle)
    {
        Assert.Equal(handle, ElementRegistry.WindowHandleOf(refId));
    }

    [Fact]
    public void PartialSnapshotWithoutKeys_KeepsNumberingAfterExistingRefs()
    {
        var table = new RefTable<string>();
        Snapshot(table, "w1", ("a", null), ("b", null), ("c", null));

        table.BeginSnapshot("w1", partial: true);
        var sub = table.Register("w1", "b-sub", null);
        table.CompleteSnapshot("w1", truncated: true);

        Assert.Equal("w1e4", sub);
        Assert.Equal("a", table.Get("w1e1"));
        Assert.Equal("c", table.Get("w1e3"));
    }
}
