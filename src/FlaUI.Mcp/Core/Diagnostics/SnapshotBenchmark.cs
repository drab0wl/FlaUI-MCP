using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace PlaywrightWindows.Mcp.Core.Diagnostics;

/// <summary>
/// <c>FlaUI.Mcp.exe --bench-snapshot "Visual Studio" [iterations]</c>: times each snapshot mode
/// against a real window and checks they produce the same text. Writes to stdout (this is not
/// MCP mode).
/// </summary>
public static class SnapshotBenchmark
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: FlaUI.Mcp --bench-snapshot <window title substring> [iterations=5] [maxDepth=10]");
            return 2;
        }

        var title = args[0];
        var iterations = args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 ? n : 5;
        var maxDepth = args.Length > 2 && int.TryParse(args[2], out var d) && d > 0 ? d : 10;
        TimingLog.Shared = new TimingLog(Console.Error, enabled: false);

        using var automation = new UIA3Automation();
        var window = automation.GetDesktop()
            .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .FirstOrDefault(w => (w.Properties.Name.ValueOrDefault ?? "").Contains(title, StringComparison.OrdinalIgnoreCase));
        if (window == null)
        {
            Console.WriteLine($"No top-level window with \"{title}\" in its title.");
            return 1;
        }
        Console.WriteLine($"Window: \"{window.Properties.Name.ValueOrDefault}\" (pid {window.Properties.ProcessId.ValueOrDefault}), " +
                          $"{iterations} iterations, maxDepth {maxDepth}");

        var runs = new (string Label, SnapshotMode Mode, bool RuntimeIds)[]
        {
            ("original (live, no runtime ids)", SnapshotMode.Live, false),
            ("live + runtime ids", SnapshotMode.Live, true),
            ("cached (per level)", SnapshotMode.Cached, true),
            ("cached (whole subtree)", SnapshotMode.CachedSubtree, true),
        };

        string? reference = null;
        foreach (var (label, mode, runtimeIds) in runs)
        {
            var times = new List<double>();
            SnapshotResult? last = null;
            for (var i = 0; i <= iterations; i++)
            {
                // Fresh registry each time so refs number from 1 and texts are comparable.
                var builder = new SnapshotBuilder(new ElementRegistry(), maxDepth, mode) { LiveRuntimeIds = runtimeIds };
                var sw = Stopwatch.StartNew();
                last = builder.Build("w1", window);
                if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds); // i == 0 is a warm-up
            }

            times.Sort();
            reference ??= last!.Text;
            var same = last!.Text == reference ? "same output" : "OUTPUT DIFFERS from original";
            Console.WriteLine($"{label,-34} median {times[times.Count / 2],8:0.0}ms  min {times[0],8:0.0}ms  " +
                              $"max {times[^1],8:0.0}ms  nodes {last.Nodes,6}  {same}  (ran as {last.Mode})");
        }
        return 0;
    }
}
