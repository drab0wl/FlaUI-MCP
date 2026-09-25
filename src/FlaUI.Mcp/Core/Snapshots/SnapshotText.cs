using System.Text;
using System.Text.RegularExpressions;

namespace PlaywrightWindows.Mcp.Core.Snapshots;

/// <summary>One element line of a snapshot: <c>{indent}- {Content}</c>.</summary>
public sealed record SnapshotLine(int Depth, string Content, string? Ref)
{
    /// <summary>States come after the ref; the quoted name comes before it, so it can't fake one.</summary>
    public bool HasState(string state)
    {
        var at = Content.IndexOf("[ref=", StringComparison.Ordinal);
        return at >= 0 && Content.IndexOf($" [{state}]", at, StringComparison.Ordinal) >= 0;
    }

    public bool IsOffscreen => HasState("offscreen");

    /// <summary>A "group" line with no name: a layout wrapper.</summary>
    public bool IsUnnamedGroup => Content.StartsWith("group [ref=", StringComparison.Ordinal);

    public string Render(int depth) => $"{SnapshotFormat.Indent(depth)}- {Content}";

    /// <summary>The content without the [offscreen] state, to tell real changes from scrolling.</summary>
    public string ContentIgnoringOffscreen => Content.Replace(" [offscreen]", "", StringComparison.Ordinal);
}

/// <summary>A parsed snapshot. Lines that aren't elements (e.g. the truncation note) go in <see cref="Notes"/>.</summary>
public sealed class ParsedSnapshot
{
    private static readonly Regex RefPattern = new(@"\[ref=([^\]\s]+)\]", RegexOptions.Compiled);

    private ParsedSnapshot(List<SnapshotLine> lines, List<string> notes)
    {
        Lines = lines;
        Notes = notes;
    }

    public IReadOnlyList<SnapshotLine> Lines { get; }
    public IReadOnlyList<string> Notes { get; }

    public static ParsedSnapshot Parse(string text)
    {
        var lines = new List<SnapshotLine>();
        var notes = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var indent = line.Length - line.TrimStart(' ').Length;
            var rest = line[indent..];
            if (!rest.StartsWith("- ", StringComparison.Ordinal))
            {
                notes.Add(line.Trim());
                continue;
            }
            var content = rest[2..];
            var match = RefPattern.Match(content);
            lines.Add(new SnapshotLine(indent / 2, content, match.Success ? match.Groups[1].Value : null));
        }
        return new ParsedSnapshot(lines, notes);
    }

    /// <summary>Index of each line's parent line, or -1.</summary>
    public int[] Parents()
    {
        var parents = new int[Lines.Count];
        var stack = new List<int>();
        for (var i = 0; i < Lines.Count; i++)
        {
            while (stack.Count > 0 && Lines[stack[^1]].Depth >= Lines[i].Depth) stack.RemoveAt(stack.Count - 1);
            parents[i] = stack.Count > 0 ? stack[^1] : -1;
            stack.Add(i);
        }
        return parents;
    }
}

public sealed record CompactResult(IReadOnlyList<(SnapshotLine Line, int Depth)> Lines, int HiddenOffscreen, int FlattenedGroups);

/// <summary>
/// Text-level transforms of a snapshot, so the same full snapshot can be shown compactly,
/// trimmed, or compared with the previous one.
/// </summary>
public static class SnapshotText
{
    /// <summary>
    /// Drops offscreen elements (with everything inside them) and unnamed groups that only wrap
    /// one visible element (the element moves up a level), and empty unnamed groups. The root
    /// line always stays.
    /// </summary>
    public static CompactResult Compact(ParsedSnapshot snapshot)
    {
        var lines = snapshot.Lines;
        var parents = snapshot.Parents();
        var children = new List<int>[lines.Count];
        for (var i = 0; i < lines.Count; i++) children[i] = new List<int>();
        var roots = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (parents[i] >= 0) children[parents[i]].Add(i);
            else roots.Add(i);
        }

        var output = new List<(SnapshotLine, int)>();
        var hidden = 0;
        var flattened = 0;

        int SubtreeSize(int i) => 1 + children[i].Sum(SubtreeSize);

        void Visit(int i, int depth, bool isRoot)
        {
            var line = lines[i];
            if (!isRoot && line.IsOffscreen)
            {
                hidden += SubtreeSize(i);
                return;
            }

            var visible = children[i].Where(c => !lines[c].IsOffscreen).ToList();
            if (!isRoot && line.IsUnnamedGroup && visible.Count <= 1)
            {
                hidden += children[i].Where(c => lines[c].IsOffscreen).Sum(SubtreeSize);
                flattened++;
                if (visible.Count == 1) Visit(visible[0], depth, isRoot: false);
                return;
            }

            output.Add((line, depth));
            foreach (var c in children[i]) Visit(c, depth + 1, isRoot: false);
        }

        foreach (var r in roots) Visit(r, lines[r].Depth, isRoot: true);
        return new CompactResult(output, hidden, flattened);
    }

    public static string Render(IEnumerable<(SnapshotLine Line, int Depth)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (line, depth) in lines) sb.AppendLine(line.Render(depth));
        return sb.ToString();
    }

    /// <summary>A snapshot shown compactly, with a note about what was left out.</summary>
    public static string CompactText(string snapshot, int maxNodes = int.MaxValue, int maxChars = int.MaxValue)
    {
        var parsed = ParsedSnapshot.Parse(snapshot);
        var compact = Compact(parsed);
        var sb = new StringBuilder(Trim(compact.Lines, maxNodes, maxChars, out _));
        foreach (var note in parsed.Notes) sb.AppendLine(note);
        if (compact.HiddenOffscreen > 0)
        {
            sb.AppendLine($"({compact.HiddenOffscreen} offscreen elements hidden; windows_snapshot with compact=false shows them)");
        }
        return sb.ToString();
    }

    /// <summary>The first lines that fit, with a truncation note when some didn't.</summary>
    public static string Trim(IReadOnlyList<(SnapshotLine Line, int Depth)> lines, int maxNodes, int maxChars, out bool truncated)
    {
        var sb = new StringBuilder();
        var count = 0;
        truncated = false;
        foreach (var (line, depth) in lines)
        {
            var text = line.Render(depth);
            if (count >= maxNodes || (count > 0 && sb.Length + text.Length + 1 > maxChars))
            {
                truncated = true;
                sb.AppendLine(SnapshotFormat.TruncationNote(count,
                    count >= maxNodes ? $"limit of {maxNodes} elements" : $"limit of {maxChars} characters"));
                break;
            }
            sb.AppendLine(text);
            count++;
        }
        return sb.ToString();
    }
}

public sealed record SnapshotDiff(
    IReadOnlyList<(SnapshotLine Line, int Depth, SnapshotLine? Context)> Added,
    IReadOnlyList<(SnapshotLine Line, int Inside)> Removed,
    IReadOnlyList<(SnapshotLine Before, SnapshotLine After)> Changed,
    int HiddenOffscreenAdded,
    int Unchanged)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    public string Summary =>
        IsEmpty ? "no changes" : $"{Added.Count} added, {Removed.Count} removed, {Changed.Count} changed";

    /// <summary>
    /// Compares two full snapshots of a window by ref (refs are stable across snapshots).
    /// An added or removed subtree is reported once, at its top; offscreen elements that
    /// appeared, and changes that are only the [offscreen] flag, are left out.
    /// </summary>
    public static SnapshotDiff Compute(ParsedSnapshot before, ParsedSnapshot after)
    {
        var beforeByRef = new Dictionary<string, int>();
        for (var i = 0; i < before.Lines.Count; i++)
        {
            if (before.Lines[i].Ref is { } r) beforeByRef.TryAdd(r, i);
        }
        var afterRefs = new HashSet<string>(after.Lines.Where(l => l.Ref != null).Select(l => l.Ref!));

        var afterParents = after.Parents();
        var isNew = new bool[after.Lines.Count];
        var isHidden = new bool[after.Lines.Count];
        var added = new List<(SnapshotLine, int, SnapshotLine?)>();
        var changed = new List<(SnapshotLine, SnapshotLine)>();
        var hiddenAdded = 0;
        var unchanged = 0;

        for (var i = 0; i < after.Lines.Count; i++)
        {
            var line = after.Lines[i];
            var parent = afterParents[i];
            if (line.Ref == null || !beforeByRef.TryGetValue(line.Ref, out var b))
            {
                isNew[i] = true;
                if (line.IsOffscreen || (parent >= 0 && isNew[parent] && isHidden[parent]))
                {
                    isHidden[i] = true;
                    hiddenAdded++;
                    continue;
                }
                // Top of a new subtree: show where it went. Inside one: indent under it.
                if (parent >= 0 && isNew[parent])
                {
                    var top = parent;
                    while (afterParents[top] >= 0 && isNew[afterParents[top]]) top = afterParents[top];
                    added.Add((line, line.Depth - after.Lines[top].Depth, null));
                }
                else
                {
                    added.Add((line, 0, parent >= 0 ? Context(after, afterParents, parent) : null));
                }
                continue;
            }

            if (before.Lines[b].ContentIgnoringOffscreen != line.ContentIgnoringOffscreen)
            {
                changed.Add((before.Lines[b], line));
            }
            else
            {
                unchanged++;
            }
        }

        var beforeParents = before.Parents();
        var gone = new bool[before.Lines.Count];
        var removed = new List<(SnapshotLine, int)>();
        var reportedAt = new Dictionary<int, int>(); // before-line index -> index in removed
        for (var i = 0; i < before.Lines.Count; i++)
        {
            var line = before.Lines[i];
            gone[i] = line.Ref == null || !afterRefs.Contains(line.Ref);
            if (!gone[i]) continue;
            var parent = beforeParents[i];
            if (parent >= 0 && gone[parent])
            {
                // Count it under the removed ancestor that is reported.
                var top = parent;
                while (beforeParents[top] >= 0 && gone[beforeParents[top]]) top = beforeParents[top];
                if (reportedAt.TryGetValue(top, out var at)) removed[at] = (removed[at].Item1, removed[at].Item2 + 1);
                continue;
            }
            reportedAt[i] = removed.Count;
            removed.Add((line, 0));
        }

        return new SnapshotDiff(added, removed, changed, hiddenAdded, unchanged);
    }

    /// <summary>Nearest ancestor worth naming as the place new elements appeared.</summary>
    private static SnapshotLine Context(ParsedSnapshot snapshot, int[] parents, int index)
    {
        var i = index;
        while (snapshot.Lines[i].IsUnnamedGroup && parents[i] >= 0) i = parents[i];
        return snapshot.Lines[i];
    }

    public string Render(int maxChars = int.MaxValue)
    {
        var sb = new StringBuilder();
        var omitted = 0;

        bool Fits(string text)
        {
            if (sb.Length + text.Length + 1 <= maxChars) return true;
            omitted++;
            return false;
        }

        if (Added.Count > 0)
        {
            sb.AppendLine("added:");
            SnapshotLine? lastContext = null;
            foreach (var (line, depth, context) in Added)
            {
                if (context != null && !ReferenceEquals(context, lastContext))
                {
                    var header = $"  in {context.Content}:";
                    if (Fits(header)) sb.AppendLine(header);
                    lastContext = context;
                }
                var text = $"{SnapshotFormat.Indent(depth + 2)}- {line.Content}";
                if (Fits(text)) sb.AppendLine(text);
            }
        }
        if (Removed.Count > 0)
        {
            sb.AppendLine("removed:");
            foreach (var (line, inside) in Removed)
            {
                var text = $"  - {line.Content}{(inside > 0 ? $" (and {inside} inside it)" : "")}";
                if (Fits(text)) sb.AppendLine(text);
            }
        }
        if (Changed.Count > 0)
        {
            sb.AppendLine("changed:");
            foreach (var (was, now) in Changed)
            {
                var text = $"  - {now.Content}  (was: {was.Content})";
                if (Fits(text)) sb.AppendLine(text);
            }
        }
        if (omitted > 0)
        {
            sb.AppendLine($"... ({omitted} more changes not shown; call windows_snapshot for the full tree)");
        }
        if (HiddenOffscreenAdded > 0)
        {
            sb.AppendLine($"({HiddenOffscreenAdded} new offscreen elements not shown)");
        }
        return sb.ToString();
    }
}
