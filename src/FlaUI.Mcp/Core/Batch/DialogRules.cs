using System.Text.Json;

namespace PlaywrightWindows.Mcp.Core.Batch;

/// <summary>
/// A standing answer for a dialog that may pop up during a batch or flow, e.g.
/// {"titleContains": "Save changes", "press": "no"}. At least one condition is required.
/// </summary>
public sealed record DialogRule(string? Title, string? TitleContains, string? TextContains, string Press)
{
    public string Describe() =>
        string.Join(" ", new[]
        {
            Title != null ? $"title \"{Title}\"" : null,
            TitleContains != null ? $"title*=\"{TitleContains}\"" : null,
            TextContains != null ? $"text*=\"{TextContains}\"" : null,
        }.Where(p => p != null)) + $" -> press \"{Press}\"";
}

public static class DialogRules
{
    public static IReadOnlyList<DialogRule> Parse(JsonElement? container, out string? error, string property = "onDialog")
    {
        error = null;
        var rules = new List<DialogRule>();
        if (container is not { ValueKind: JsonValueKind.Object } c || !c.TryGetProperty(property, out var list)) return rules;
        if (list.ValueKind == JsonValueKind.Object) list = JsonSerializer.SerializeToElement(new[] { list });
        if (list.ValueKind != JsonValueKind.Array)
        {
            error = $"{property} must be a list of rules like {{\"titleContains\": \"Save changes\", \"press\": \"no\"}}";
            return rules;
        }

        foreach (var r in list.EnumerateArray())
        {
            string? Str(string name) =>
                r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            var rule = new DialogRule(Str("title"), Str("titleContains"), Str("textContains"), Str("press") ?? "");
            if (rule.Press.Length == 0)
            {
                error = $"Each {property} rule needs \"press\" (a button: text, or ok/cancel/yes/no...).";
                return Array.Empty<DialogRule>();
            }
            if (rule.Title == null && rule.TitleContains == null && rule.TextContains == null)
            {
                error = $"Each {property} rule needs title, titleContains or textContains, so it can't answer every dialog.";
                return Array.Empty<DialogRule>();
            }
            rules.Add(rule);
        }
        return rules;
    }

    /// <summary>The first rule every condition of which holds for this dialog, or null.</summary>
    public static DialogRule? Match(IReadOnlyList<DialogRule> rules, string title, string text)
    {
        foreach (var rule in rules)
        {
            if (rule.Title != null && !NameMatch.LooselyEquals(rule.Title, title)) continue;
            if (rule.TitleContains != null && !title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.TextContains != null && !text.Contains(rule.TextContains, StringComparison.OrdinalIgnoreCase)) continue;
            return rule;
        }
        return null;
    }
}
