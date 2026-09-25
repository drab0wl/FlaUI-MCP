using System.Text;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Builds the status block appended to tool results so the agent can't miss a dialog that
/// popped up, or an earlier action that finished in the background.
/// </summary>
public static class StatusFooter
{
    public const string Header = "--- status ---";

    /// <returns>Null when there is nothing to report.</returns>
    public static string? Build(
        IReadOnlyList<DialogInfo> dialogs,
        Func<DialogInfo, string> handleFor,
        IReadOnlyList<PendingOperation> running,
        IReadOnlyList<PendingOperation> finished,
        DateTime? nowUtc = null)
    {
        var blocking = dialogs.Where(d => d.IsBlocking).ToList();
        if (blocking.Count == 0 && running.Count == 0 && finished.Count == 0)
        {
            return null;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine(Header);

        if (blocking.Count > 0)
        {
            sb.AppendLine("Open dialogs:");
            foreach (var d in blocking)
            {
                sb.AppendLine($"- {handleFor(d)} {DialogClassifier.Describe(d)} (pid {d.ProcessId})");
            }
        }

        if (running.Count > 0)
        {
            sb.AppendLine("Pending actions (waiting on a dialog or a busy app):");
            foreach (var op in running)
            {
                var secs = Math.Max(0, (now - op.StartedUtc).TotalSeconds);
                sb.AppendLine($"- {op.Id}: {op.Description} (running {secs:0.0}s)");
            }
        }

        if (finished.Count > 0)
        {
            sb.AppendLine("Finished since last call:");
            foreach (var op in finished)
            {
                sb.AppendLine($"- {op.Id}: {op.Description} -> {op.FinishedText}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
