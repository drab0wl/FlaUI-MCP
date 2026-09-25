using System.Diagnostics;
using PlaywrightWindows.Mcp.Core.Dialogs;

namespace PlaywrightWindows.Mcp.Core.Actions;

public enum ActionState
{
    /// <summary>The action returned (and no dialog appeared, or one appeared after it returned).</summary>
    Completed,
    /// <summary>The action threw.</summary>
    Faulted,
    /// <summary>A dialog appeared while the action was still running; the action is now a pending operation.</summary>
    DialogOpened,
    /// <summary>The action did not return within the wait window and no dialog appeared.</summary>
    StillRunning,
}

public sealed record ActionOutcome(
    ActionState State,
    string Description,
    string? Result,
    Exception? Error,
    PendingOperation? Operation,
    IReadOnlyList<DialogInfo> NewDialogs);

public sealed record ActionRunOptions
{
    /// <summary>How long to wait for the action to return before handing back a pending operation.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// After the action returns, keep watching this long for dialogs. Many providers (WPF's Invoke,
    /// SendInput clicks) return before the click handler runs, so the dialog shows up slightly later.
    /// </summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// An action whose UIA call has not returned yet, typically because the app opened a modal
/// dialog from inside the handler. It completes when the dialog closes.
/// </summary>
public sealed class PendingOperation
{
    internal PendingOperation(string id, string description, int processId, Task<string> completion)
    {
        Id = id;
        Description = description;
        ProcessId = processId;
        Completion = completion;
        StartedUtc = DateTime.UtcNow;
    }

    public string Id { get; }
    public string Description { get; }
    public int ProcessId { get; }
    public DateTime StartedUtc { get; }
    public Task<string> Completion { get; }
    public bool IsFinished => Completion.IsCompleted;

    /// <summary>Result text or error message once finished; null while running.</summary>
    public string? FinishedText =>
        !Completion.IsCompleted ? null
        : Completion.IsCompletedSuccessfully ? Completion.Result
        : $"error: {ActionRunner.Unwrap(Completion.Exception)?.Message ?? "cancelled"}";
}

/// <summary>
/// Tracks pending operations so later tool calls can report when they finish.
/// </summary>
public sealed class PendingOperationRegistry
{
    private readonly object _gate = new();
    private readonly List<PendingOperation> _ops = new();
    private int _counter;

    public PendingOperation Register(string description, int processId, Task<string> completion)
    {
        lock (_gate)
        {
            var op = new PendingOperation($"op{++_counter}", description, processId, completion);
            _ops.Add(op);
            return op;
        }
    }

    public PendingOperation? Get(string id)
    {
        lock (_gate) return _ops.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<PendingOperation> Running()
    {
        lock (_gate) return _ops.Where(o => !o.IsFinished).ToList();
    }

    public bool HasRunningFor(int processId)
    {
        lock (_gate) return _ops.Any(o => !o.IsFinished && o.ProcessId == processId);
    }

    /// <summary>
    /// Returns operations that have finished since the last call and forgets them,
    /// so each completion is reported exactly once.
    /// </summary>
    public IReadOnlyList<PendingOperation> TakeFinished()
    {
        lock (_gate)
        {
            var done = _ops.Where(o => o.IsFinished).ToList();
            _ops.RemoveAll(o => o.IsFinished);
            return done;
        }
    }

    /// <summary>Forget a finished operation that has already been reported by other means.</summary>
    public void Forget(PendingOperation op)
    {
        lock (_gate)
        {
            if (op.IsFinished) _ops.Remove(op);
        }
    }
}

/// <summary>
/// Runs a potentially blocking UI action on a dedicated thread and races it against
/// dialog detection, so a click that opens a modal dialog returns immediately instead of
/// hanging until the dialog is dismissed.
/// </summary>
public sealed class ActionRunner
{
    private readonly PendingOperationRegistry _registry;

    public ActionRunner(PendingOperationRegistry registry)
    {
        _registry = registry;
    }

    /// <param name="probeNewDialogs">
    /// Returns dialogs that appeared since the action started. Must not use UI Automation
    /// (the provider may be the thing that's blocked).
    /// </param>
    public async Task<ActionOutcome> RunAsync(
        string description,
        int processId,
        Func<string> action,
        Func<IReadOnlyList<DialogInfo>> probeNewDialogs,
        ActionRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ActionRunOptions();
        var completion = StartOnDedicatedThread(action, description);
        var sw = Stopwatch.StartNew();
        TimeSpan? returnedAt = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dialogs = SafeProbe(probeNewDialogs);
            if (dialogs.Count > 0)
            {
                if (completion.IsCompleted)
                {
                    return Finished(description, completion, dialogs);
                }

                var op = _registry.Register(description, processId, completion);
                return new ActionOutcome(ActionState.DialogOpened, description, null, null, op, dialogs);
            }

            if (completion.IsCompleted)
            {
                returnedAt ??= sw.Elapsed;
                if (sw.Elapsed - returnedAt.Value >= options.SettleTime)
                {
                    return Finished(description, completion, Array.Empty<DialogInfo>());
                }
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (sw.Elapsed >= options.WaitTimeout)
            {
                var op = _registry.Register(description, processId, completion);
                return new ActionOutcome(ActionState.StillRunning, description, null, null, op, Array.Empty<DialogInfo>());
            }

            await Task.WhenAny(completion, Task.Delay(options.PollInterval, cancellationToken)).ConfigureAwait(false);
        }
    }

    private static ActionOutcome Finished(string description, Task<string> completion, IReadOnlyList<DialogInfo> dialogs)
    {
        return completion.IsCompletedSuccessfully
            ? new ActionOutcome(ActionState.Completed, description, completion.Result, null, null, dialogs)
            : new ActionOutcome(ActionState.Faulted, description, null, Unwrap(completion.Exception), null, dialogs);
    }

    private static IReadOnlyList<DialogInfo> SafeProbe(Func<IReadOnlyList<DialogInfo>> probe)
    {
        try
        {
            return probe();
        }
        catch
        {
            return Array.Empty<DialogInfo>();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a new background thread (not the thread pool, so a call
    /// blocked for minutes behind a modal dialog cannot starve other work).
    /// </summary>
    public static Task<T> StartOnDedicatedThread<T>(Func<T> action, string name)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"flaui-mcp: {name}",
        };
        if (OperatingSystem.IsWindows())
        {
            // UIA clients should not call from an STA they don't pump.
            thread.SetApartmentState(ApartmentState.MTA);
        }
        thread.Start();
        return tcs.Task;
    }

    internal static Exception? Unwrap(Exception? ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } agg)
        {
            ex = agg.InnerException;
        }
        return ex;
    }
}

/// <summary>
/// Checks whether a UIA provider answers at all, without risking the caller's thread.
/// </summary>
public static class Responsiveness
{
    /// <returns>True if <paramref name="uiaCall"/> returned (or threw) within the timeout.</returns>
    public static bool Probe(Action uiaCall, TimeSpan timeout)
    {
        var task = ActionRunner.StartOnDedicatedThread(() =>
        {
            uiaCall();
            return true;
        }, "responsiveness probe");

        try
        {
            return task.Wait(timeout) || task.IsCompleted;
        }
        catch (AggregateException)
        {
            // It answered, just with an error (element gone, etc.). Not blocked.
            return true;
        }
    }
}
