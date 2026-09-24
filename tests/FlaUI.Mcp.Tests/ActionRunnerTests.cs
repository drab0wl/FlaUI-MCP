using System.Diagnostics;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class ActionRunnerTests
{
    private static readonly DialogInfo Dialog = new(0x20, 1, 0x10, "#32770", "Confirm", true, true);

    private static ActionRunOptions Fast(int waitMs = 2000, int settleMs = 50) => new()
    {
        WaitTimeout = TimeSpan.FromMilliseconds(waitMs),
        SettleTime = TimeSpan.FromMilliseconds(settleMs),
        PollInterval = TimeSpan.FromMilliseconds(10),
    };

    private static IReadOnlyList<DialogInfo> None() => Array.Empty<DialogInfo>();

    [Fact]
    public async Task QuickAction_NoDialog_Completes()
    {
        var registry = new PendingOperationRegistry();
        var runner = new ActionRunner(registry);

        var outcome = await runner.RunAsync("click OK", 1, () => "Invoked OK", None, Fast());

        Assert.Equal(ActionState.Completed, outcome.State);
        Assert.Equal("Invoked OK", outcome.Result);
        Assert.Empty(outcome.NewDialogs);
        Assert.Empty(registry.Running());
    }

    [Fact]
    public async Task BlockingAction_DialogAppears_ReturnsImmediatelyWithPendingOp()
    {
        var registry = new PendingOperationRegistry();
        var runner = new ActionRunner(registry);
        using var release = new ManualResetEventSlim(false);
        var dialogVisible = false;

        // Simulates Invoke() whose handler calls ShowDialog(): blocks until the dialog closes.
        string BlockingInvoke()
        {
            dialogVisible = true;
            release.Wait();
            return "Invoked Delete";
        }

        var sw = Stopwatch.StartNew();
        var outcome = await runner.RunAsync(
            "click Delete", 1, BlockingInvoke,
            () => dialogVisible ? new[] { Dialog } : None(),
            Fast(waitMs: 5000));
        sw.Stop();

        Assert.Equal(ActionState.DialogOpened, outcome.State);
        Assert.Equal("Confirm", Assert.Single(outcome.NewDialogs).Title);
        Assert.NotNull(outcome.Operation);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Should not wait for the action: {sw.Elapsed}");
        Assert.True(registry.HasRunningFor(1));

        // Dialog is dismissed; the original call returns and the op finishes.
        release.Set();
        var result = await outcome.Operation!.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Invoked Delete", result);

        var finished = registry.TakeFinished();
        Assert.Equal(outcome.Operation.Id, Assert.Single(finished).Id);
        Assert.Empty(registry.TakeFinished()); // reported once
    }

    [Fact]
    public async Task AsyncProvider_DialogAppearsAfterReturn_IsCaughtBySettleWindow()
    {
        var registry = new PendingOperationRegistry();
        var runner = new ActionRunner(registry);
        var returnedAt = Stopwatch.StartNew();
        var returned = false;

        var outcome = await runner.RunAsync(
            "click Save", 1,
            () => { returned = true; returnedAt.Restart(); return "Invoked Save"; },
            () => returned && returnedAt.ElapsedMilliseconds > 30 ? new[] { Dialog } : None(),
            Fast(settleMs: 300));

        Assert.Equal(ActionState.Completed, outcome.State);
        Assert.Equal("Invoked Save", outcome.Result);
        Assert.Single(outcome.NewDialogs);
        Assert.Null(outcome.Operation);
    }

    [Fact]
    public async Task SlowAction_NoDialog_ReturnsStillRunning()
    {
        var registry = new PendingOperationRegistry();
        var runner = new ActionRunner(registry);
        using var release = new ManualResetEventSlim(false);

        var outcome = await runner.RunAsync(
            "click Build", 1, () => { release.Wait(); return "done"; }, None, Fast(waitMs: 100));

        Assert.Equal(ActionState.StillRunning, outcome.State);
        Assert.NotNull(outcome.Operation);
        Assert.False(outcome.Operation!.IsFinished);
        release.Set();
        await outcome.Operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("done", outcome.Operation.FinishedText);
    }

    [Fact]
    public async Task ThrowingAction_IsFaulted()
    {
        var runner = new ActionRunner(new PendingOperationRegistry());

        var outcome = await runner.RunAsync(
            "click X", 1, () => throw new InvalidOperationException("element gone"), None, Fast());

        Assert.Equal(ActionState.Faulted, outcome.State);
        Assert.IsType<InvalidOperationException>(outcome.Error);
    }

    [Fact]
    public async Task ThrowingProbe_IsTreatedAsNoDialogs()
    {
        var runner = new ActionRunner(new PendingOperationRegistry());

        var outcome = await runner.RunAsync(
            "click X", 1, () => "ok", () => throw new Exception("enum failed"), Fast());

        Assert.Equal(ActionState.Completed, outcome.State);
    }

    [Fact]
    public async Task FailedPendingOp_ReportsErrorText()
    {
        var registry = new PendingOperationRegistry();
        var runner = new ActionRunner(registry);
        using var release = new ManualResetEventSlim(false);

        var outcome = await runner.RunAsync(
            "click Y", 1,
            () => { release.Wait(); throw new TimeoutException("UIA gave up"); },
            () => new[] { Dialog },
            Fast());

        release.Set();
        await Assert.ThrowsAsync<TimeoutException>(() => outcome.Operation!.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("error: UIA gave up", outcome.Operation!.FinishedText);
    }

    [Fact]
    public void Responsiveness_DetectsBlockedCall()
    {
        using var block = new ManualResetEventSlim(false);
        Assert.False(Responsiveness.Probe(() => block.Wait(), TimeSpan.FromMilliseconds(100)));
        block.Set();

        Assert.True(Responsiveness.Probe(() => { }, TimeSpan.FromSeconds(1)));
        Assert.True(Responsiveness.Probe(() => throw new Exception("stale element"), TimeSpan.FromSeconds(1)));
    }
}
