using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Tests for windows_snapshot tool using the WinForms and WPF test apps.
/// </summary>
[Collection("TestApps")]
public class SnapshotTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SnapshotTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void WinFormsApp_IsRunning()
    {
        Assert.NotEmpty(_fixture.WinFormsHandle);
        var window = _fixture.GetWinFormsWindow();
        Assert.NotNull(window);
        _output.WriteLine($"WinForms window: {window.Title} (handle: {_fixture.WinFormsHandle})");
    }

    [Fact]
    public void WpfApp_IsRunning()
    {
        Assert.NotEmpty(_fixture.WpfHandle);
        var window = _fixture.GetWpfWindow();
        Assert.NotNull(window);
        _output.WriteLine($"WPF window: {window.Title} (handle: {_fixture.WpfHandle})");
    }

    [Fact]
    public void WinForms_Snapshot_ContainsTabControl()
    {
        var builder = new SnapshotBuilder(_fixture.Elements);
        var snapshot = builder.BuildSnapshot(_fixture.WinFormsHandle, _fixture.GetWinFormsWindow()!);
        _output.WriteLine(snapshot);

        Assert.Contains("Buttons", snapshot);
        Assert.Contains("Forms", snapshot);
        Assert.Contains("Grid", snapshot);
        Assert.Contains("Trees", snapshot);
        Assert.Contains("Dialogs", snapshot);
    }

    [Fact]
    public void Wpf_Snapshot_ContainsTabControl()
    {
        var builder = new SnapshotBuilder(_fixture.Elements);
        var snapshot = builder.BuildSnapshot(_fixture.WpfHandle, _fixture.GetWpfWindow()!);
        _output.WriteLine(snapshot);

        Assert.Contains("Buttons", snapshot);
        Assert.Contains("Forms", snapshot);
        Assert.Contains("Grid", snapshot);
        Assert.Contains("Trees", snapshot);
    }

    [Fact]
    public async Task WinForms_Snapshot_ContainsButtons()
    {
        // Navigate to Buttons tab first — another test may have switched tabs
        var snapshot = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
        var tabRef = TestAppFixture.FindRefInSnapshot(snapshot, "Buttons");
        if (tabRef != null)
        {
            var clickTool = _fixture.CreateClickTool();
            await _fixture.CallTool(clickTool, new { @ref = tabRef });
            await Task.Delay(100);
        }

        var snapshot2 = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
        Assert.Contains("Click Me", snapshot2);
        Assert.Contains("Conditional Button", snapshot2);
    }

    [Fact]
    public async Task WinForms_Snapshot_ContainsGridData()
    {
        // Navigate to Grid tab first — WinForms only shows active tab content
        var snapshot = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
        var gridTabRef = TestAppFixture.FindRefInSnapshot(snapshot, "Grid");

        Assert.NotNull(gridTabRef);

        var clickTool = _fixture.CreateClickTool();
        await _fixture.CallTool(clickTool, new { @ref = gridTabRef });

        // Poll for grid content to appear after tab switch
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string snapshot2 = "";
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
            snapshot2 = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
            if (snapshot2.Contains("Test Data"))
                break;
        }

        _output.WriteLine(snapshot2[..Math.Min(2000, snapshot2.Length)]);
        Assert.Contains("Test Data", snapshot2);
    }

    [Theory]
    [InlineData("winforms")]
    [InlineData("wpf")]
    public void CachedSnapshots_MatchTheOriginalLiveWalk(string app)
    {
        var handle = app == "winforms" ? _fixture.WinFormsHandle : _fixture.WpfHandle;
        var window = _fixture.Session.GetWindow(handle)!;

        SnapshotResult Build(SnapshotMode mode) =>
            new SnapshotBuilder(new ElementRegistry(), mode: mode) { LiveRuntimeIds = false }.Build(handle, window);

        var live = Build(SnapshotMode.Live);
        var cached = Build(SnapshotMode.Cached);
        var subtree = Build(SnapshotMode.CachedSubtree);
        _output.WriteLine($"live {live.Elapsed.TotalMilliseconds:0}ms, cached {cached.Elapsed.TotalMilliseconds:0}ms, " +
                          $"subtree {subtree.Elapsed.TotalMilliseconds:0}ms, {live.Nodes} nodes");

        Assert.Equal(SnapshotMode.Cached, cached.Mode);
        Assert.Equal(live.Text, cached.Text);
        Assert.Equal(live.Text, subtree.Text);
    }

    [Fact]
    public void Resnapshot_KeepsRefsStable()
    {
        var first = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
        var second = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
        Assert.Equal(first, second);
    }

    [Fact]
    public void BoundedSnapshot_IsTruncated_AndKeepsOtherRefs()
    {
        var handle = _fixture.WinFormsHandle;
        var full = _fixture.TakeSnapshot(handle);
        var lastRef = full.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\[ref=(w\d+e\d+)\]"))
            .Last(m => m.Success).Groups[1].Value;

        var window = _fixture.Session.GetWindow(handle)!;
        var bounded = new SnapshotBuilder(_fixture.Elements).Build(handle, window,
            new PlaywrightWindows.Mcp.Core.Snapshots.SnapshotLimits { MaxNodes = 5 });
        _output.WriteLine(bounded.Text);

        Assert.True(bounded.Truncated);
        Assert.Equal(5, bounded.Nodes);
        Assert.Contains("snapshot truncated after 5 elements", bounded.Text);
        Assert.NotNull(_fixture.Elements.GetElement(lastRef));
    }
}
