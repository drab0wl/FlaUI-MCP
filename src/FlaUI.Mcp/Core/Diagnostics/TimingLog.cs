using System.Diagnostics;
using System.Text;

namespace PlaywrightWindows.Mcp.Core.Diagnostics;

/// <summary>
/// Per-tool timing written to stderr (never stdout: that is the MCP channel), so a session's
/// time can be split into tool execution vs. the model's turns between calls.
/// Disable with FLAUI_MCP_TIMING=0.
/// </summary>
/// <remarks>
/// "gap" is the time from the previous tool result being handed back to this call arriving:
/// the model's turn plus transport. It is only meaningful when calls are sequential.
/// </remarks>
public sealed class TimingLog
{
    public static TimingLog Shared { get; set; } = new(
        Console.Error,
        !string.Equals(Environment.GetEnvironmentVariable("FLAUI_MCP_TIMING"), "0", StringComparison.Ordinal));

    private readonly object _gate = new();
    private readonly TextWriter _writer;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, ToolStats> _stats = new(StringComparer.Ordinal);
    private long? _lastResultTicks;
    private double _totalGapMs;
    private int _gaps;

    public TimingLog(TextWriter writer, bool enabled = true, Func<long>? clock = null)
    {
        _writer = writer;
        Enabled = enabled;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    public bool Enabled { get; }

    /// <summary>Call when a tool call arrives. Returns the gap since the previous result, if any.</summary>
    public TimeSpan? BeginCall()
    {
        lock (_gate)
        {
            if (_lastResultTicks is not { } last) return null;
            var gap = Stopwatch.GetElapsedTime(last, _clock());
            _totalGapMs += gap.TotalMilliseconds;
            _gaps++;
            return gap;
        }
    }

    /// <summary>Call when a tool result is handed back.</summary>
    public void EndCall(string tool, TimeSpan execution, TimeSpan annotation, TimeSpan? gap, bool isError)
    {
        bool summarize;
        lock (_gate)
        {
            _lastResultTicks = _clock();
            if (!_stats.TryGetValue(tool, out var s)) _stats[tool] = s = new ToolStats();
            s.Calls++;
            s.TotalMs += execution.TotalMilliseconds;
            s.AnnotateMs += annotation.TotalMilliseconds;
            s.MaxMs = Math.Max(s.MaxMs, execution.TotalMilliseconds);
            if (isError) s.Errors++;
            // Clients often kill the server rather than closing stdin, so don't rely on an exit summary.
            summarize = SummaryEvery > 0 && _stats.Values.Sum(x => x.Calls) % SummaryEvery == 0;
        }

        Write($"tool={tool} exec={Ms(execution)} status={Ms(annotation)} " +
              $"gap={(gap is { } g ? Ms(g) : "-")}{(isError ? " error" : "")}");
        if (summarize) WriteSummary();
    }

    /// <summary>Write the summary every N calls (0 = only at exit).</summary>
    public int SummaryEvery { get; init; } = 20;

    /// <summary>Snapshot builds, selector lookups and other sub-steps.</summary>
    public void Detail(string what, TimeSpan elapsed, string? extra = null)
    {
        Write($"{what} {Ms(elapsed)}{(extra != null ? " " + extra : "")}");
    }

    /// <summary>Totals per tool plus total model-turn gap, for the end of a session.</summary>
    public string Summary()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            var toolMs = _stats.Values.Sum(s => s.TotalMs + s.AnnotateMs);
            sb.AppendLine($"[timing] summary: tool time {toolMs:0}ms over {_stats.Values.Sum(s => s.Calls)} calls; " +
                          $"gaps between calls (model turns) {_totalGapMs:0}ms over {_gaps}");
            foreach (var (name, s) in _stats.OrderByDescending(kv => kv.Value.TotalMs))
            {
                sb.AppendLine($"[timing]   {name,-22} calls={s.Calls,-4} total={s.TotalMs:0}ms " +
                              $"avg={s.TotalMs / s.Calls:0}ms max={s.MaxMs:0}ms status={s.AnnotateMs:0}ms errors={s.Errors}");
            }
            return sb.ToString().TrimEnd();
        }
    }

    public void WriteSummary()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (_stats.Count == 0) return;
        }
        WriteRaw(Summary());
    }

    private void Write(string line)
    {
        if (!Enabled) return;
        WriteRaw($"[timing] {line}");
    }

    private void WriteRaw(string text)
    {
        try
        {
            lock (_writer) _writer.WriteLine(text);
        }
        catch
        {
            // Logging must never break a tool call.
        }
    }

    private static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:0}ms";

    private sealed class ToolStats
    {
        public int Calls;
        public int Errors;
        public double TotalMs;
        public double AnnotateMs;
        public double MaxMs;
    }
}
