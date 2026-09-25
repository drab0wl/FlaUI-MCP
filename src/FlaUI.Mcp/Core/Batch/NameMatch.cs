using System.Text;

namespace PlaywrightWindows.Mcp.Core.Batch;

/// <summary>
/// Forgiving name comparison for selectors and menu paths. Agents write "Save As" for
/// "Save As...", "OK" for "&amp;OK", "Name" for "Name:". Pure, so it's unit tested.
/// </summary>
public static class NameMatch
{
    /// <summary>
    /// Lower case, accelerator ampersands and trailing "..." / ":" / "…" removed, whitespace
    /// collapsed, and a trailing shortcut ("Save\tCtrl+S") dropped.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var s = name;
        var tab = s.IndexOf('\t');
        if (tab >= 0) s = s[..tab];

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '&')
            {
                // "&&" is a literal ampersand.
                if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
        }

        var result = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        while (true)
        {
            var trimmed = result.TrimEnd('.', ':', '…', ' ');
            if (trimmed == result) break;
            result = trimmed;
        }
        return result;
    }

    public static bool LooselyEquals(string? a, string? b)
    {
        var na = Normalize(a);
        return na.Length > 0 && na == Normalize(b);
    }

    /// <summary>
    /// 0..1: how close <paramref name="candidate"/> is to what was asked for. 1 for a loose
    /// match; high for prefixes, containment and shared words; otherwise edit distance.
    /// </summary>
    public static double Score(string wanted, string? candidate)
    {
        var w = Normalize(wanted);
        var c = Normalize(candidate);
        if (w.Length == 0 || c.Length == 0) return 0;
        if (w == c) return 1;
        var score = 1.0 - (double)Levenshtein(w, c) / Math.Max(w.Length, c.Length);
        if (c.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(c, StringComparison.Ordinal))
        {
            // A prefix ("Save Al" / "Save All") beats a same-length typo ("Save As").
            score = Math.Max(score, 0.6 + 0.4 * Math.Min(w.Length, c.Length) / Math.Max(w.Length, c.Length));
        }
        if (c.Contains(w, StringComparison.Ordinal) || w.Contains(c, StringComparison.Ordinal))
        {
            score = Math.Max(score, 0.75);
        }

        var wordsW = w.Split(' ');
        var wordsC = c.Split(' ');
        var shared = wordsW.Count(x => wordsC.Contains(x));
        if (shared > 0)
        {
            score = Math.Max(score, 0.5 + 0.4 * shared / Math.Max(wordsW.Length, wordsC.Length));
        }
        return score;
    }

    /// <summary>The first exact name match, else the first loose one, else null.</summary>
    public static T? Pick<T>(IEnumerable<T> items, string wanted, Func<T, string?> nameOf) where T : class
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        return list.FirstOrDefault(i => string.Equals(nameOf(i), wanted, StringComparison.Ordinal))
               ?? list.FirstOrDefault(i => LooselyEquals(wanted, nameOf(i)));
    }

    /// <summary>The best candidates for a miss, best first, above a relevance floor.</summary>
    public static IReadOnlyList<T> Suggest<T>(string wanted, IEnumerable<T> candidates, Func<T, string?> nameOf, int max = 5, double floor = 0.45)
    {
        return candidates
            .Select(c => (Candidate: c, Score: Score(wanted, nameOf(c))))
            .Where(x => x.Score >= floor)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>Menu paths: ["File", "Save As..."] or "File > Save As...". Pure.</summary>
public static class MenuPath
{
    public static IReadOnlyList<string>? Parse(System.Text.Json.JsonElement container, string property = "path")
    {
        if (container.ValueKind != System.Text.Json.JsonValueKind.Object || !container.TryGetProperty(property, out var p)) return null;
        return p.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Array => p.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .ToList(),
            System.Text.Json.JsonValueKind.String => Split(p.GetString()!),
            _ => null,
        };
    }

    /// <summary>"File > Save As..." -> ["File", "Save As..."]. "->" and "/" are not separators (names use them).</summary>
    public static IReadOnlyList<string> Split(string path) =>
        path.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.TrimEnd('-').Trim())
            .Where(s => s.Length > 0)
            .ToList();

    public static string Describe(IReadOnlyList<string> path) => string.Join(" > ", path);
}
