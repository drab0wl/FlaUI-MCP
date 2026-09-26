using System.Diagnostics;

namespace PlaywrightWindows.Mcp.Core.Waiting;

/// <summary>
/// Wakes a waiting check as soon as something changes (a UI Automation event), instead of on a
/// fixed poll. Pulses coalesce: many events while a check runs cause one more check.
/// </summary>
public sealed class ChangeSignal
{
    private readonly SemaphoreSlim _pulse = new(0, 1);
    private long _lastActivityTicks = Stopwatch.GetTimestamp();

    public void Pulse()
    {
        Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
        try { _pulse.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Time since the last pulse (or since creation).</summary>
    public TimeSpan Quiet => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityTicks));

    /// <returns>True if pulsed, false if <paramref name="max"/> passed first.</returns>
    public Task<bool> WaitAsync(TimeSpan max) => _pulse.WaitAsync(max < TimeSpan.Zero ? TimeSpan.Zero : max);
}

public static class Poll
{
    /// <summary>Checks at most this often, however many events arrive.</summary>
    public static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Runs <paramref name="condition"/> until it's true or <paramref name="timeout"/> passes.
    /// With a signal, re-checks as soon as it pulses (and every <paramref name="fallback"/> in
    /// case an event is missed); without one, every <paramref name="fallback"/>.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout, ChangeSignal? signal, TimeSpan fallback)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var started = sw.Elapsed;
            if (condition()) return true;
            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;

            var wait = fallback < remaining ? fallback : remaining;
            if (signal != null) await signal.WaitAsync(wait);
            else await Task.Delay(wait);

            var since = sw.Elapsed - started;
            if (since < MinSpacing) await Task.Delay(MinSpacing - since);
        }
    }
}

/// <summary>When an app counts as idle. Pure.</summary>
public static class Idle
{
    /// <param name="responsive">The window answered a no-op message quickly (its message loop is running).</param>
    /// <param name="progressRunning">A visible progress bar is part-way.</param>
    /// <param name="quiet">Time since the UI last changed.</param>
    public static bool IsIdle(bool responsive, bool progressRunning, TimeSpan quiet, TimeSpan stable) =>
        responsive && !progressRunning && quiet >= stable;

    /// <summary>A determinate progress bar strictly between its ends. (Marquee bars can't be told apart.)</summary>
    public static bool IsRunning(double value, double min, double max) => max > min && value > min && value < max;

    public static string Describe(bool responsive, bool progressRunning, TimeSpan quiet, TimeSpan stable)
    {
        var reasons = new List<string>();
        if (!responsive) reasons.Add("not responding to messages");
        if (progressRunning) reasons.Add("a progress bar is running");
        if (quiet < stable) reasons.Add($"the UI changed {quiet.TotalMilliseconds:0}ms ago");
        return reasons.Count == 0 ? "idle" : string.Join(", ", reasons);
    }
}
