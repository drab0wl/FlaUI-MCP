using System.Text.Json;
using System.Text.RegularExpressions;
using PlaywrightWindows.Mcp.Core.Snapshots;

namespace PlaywrightWindows.Mcp.Core.Batch;

/// <summary>
/// Finds an element by what it is rather than by a ref from an earlier snapshot.
/// </summary>
/// <param name="Handle">Window to search: a handle ("w3") or "$dialog" for the dialog the batch last saw open.</param>
/// <param name="Index">0-based, when several elements match (document order).</param>
public sealed record ElementSelector(
    string? Name = null,
    string? NameContains = null,
    string? AutomationId = null,
    string? Role = null,
    string? Handle = null,
    int Index = 0)
{
    public const string LastDialog = "$dialog";

    public bool HasCriteria => Name != null || NameContains != null || AutomationId != null || Role != null;

    /// <summary>
    /// Reads a selector from a step: its "selector" object, else the step's own
    /// name / nameContains / automationId / role fields. Null when the step has none.
    /// </summary>
    public static ElementSelector? FromStep(JsonElement step, out string? error)
    {
        error = null;
        var isNested = step.TryGetProperty("selector", out var nested) && nested.ValueKind == JsonValueKind.Object;
        var source = isNested ? nested : step;

        var selector = new ElementSelector(
            Str(source, "name"),
            Str(source, "nameContains"),
            Str(source, "automationId"),
            Str(source, "role"),
            Str(source, "handle") ?? (isNested ? Str(step, "handle") : null),
            source.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0);

        if (!selector.HasCriteria)
        {
            if (isNested) error = "selector needs name, nameContains, automationId or role";
            return null;
        }

        selector = selector.Normalize();
        if (selector.Role != null && SnapshotFormat.ControlTypesForRole(selector.Role).Count == 0)
        {
            error = $"Unknown role \"{selector.Role}\". Use a role from snapshots: {string.Join(", ", SnapshotFormat.KnownRoles)}.";
            return null;
        }
        if (selector.Index < 0)
        {
            error = "index must be >= 0";
            return null;
        }
        return selector;
    }

    /// <summary>
    /// Snapshots print elements without a Name as "[automationId]", so a name in brackets is an automation id.
    /// </summary>
    public ElementSelector Normalize()
    {
        if (AutomationId == null && Name != null)
        {
            var m = Regex.Match(Name, @"^\[(.+)\]$");
            if (m.Success) return this with { Name = null, AutomationId = m.Groups[1].Value };
        }
        return this;
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (Role != null) parts.Add(Role);
        if (Name != null) parts.Add($"\"{Name}\"");
        if (NameContains != null) parts.Add($"name*=\"{NameContains}\"");
        if (AutomationId != null) parts.Add($"#{AutomationId}");
        if (Index > 0) parts.Add($"[{Index}]");
        if (Handle != null) parts.Add($"in {Handle}");
        return string.Join(" ", parts);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}

public static class WaitConditions
{
    public const string DialogOpen = "dialog_open";
    public const string DialogClosed = "dialog_closed";
    public const string Element = "element";
    public const string ElementGone = "element_gone";
    public const string TextContains = "text_contains";

    public static readonly string[] All = { DialogOpen, DialogClosed, Element, ElementGone, TextContains };

    public static string? Normalize(string? until) => until?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "dialog_open" or "dialog_opened" => DialogOpen,
        "dialog_closed" or "dialog_close" => DialogClosed,
        "element" or "element_appears" or "appears" => Element,
        "element_gone" or "element_disappears" or "gone" => ElementGone,
        "text_contains" or "element_text_contains" => TextContains,
        _ => "?" + until,
    };
}

/// <summary>One parsed windows_batch action.</summary>
public sealed record BatchStep
{
    public required string Action { get; init; }
    public string? Ref { get; init; }
    public ElementSelector? Selector { get; init; }
    public string? SelectorError { get; init; }
    public string? Text { get; init; }
    public string? Value { get; init; }
    public string? Mode { get; init; }
    public string? Until { get; init; }
    public int? Ms { get; init; }
    public int? TimeoutMs { get; init; }
    public string? Handle { get; init; }
    public string? Button { get; init; }
    public string? Control { get; init; }
    public bool NoDialog { get; init; }
    public int? SettleMs { get; init; }

    public bool IsWaitFor(string condition) => Action == "wait" && Until == condition;

    public static BatchStep Parse(JsonElement step)
    {
        var selector = ElementSelector.FromStep(step, out var selectorError);
        return new BatchStep
        {
            Action = (Str(step, "action") ?? "").Trim().ToLowerInvariant(),
            Ref = Str(step, "ref"),
            Selector = selector,
            SelectorError = selectorError,
            Text = Str(step, "text"),
            Value = Str(step, "value"),
            Mode = Str(step, "mode"),
            Until = WaitConditions.Normalize(Str(step, "until")),
            Ms = Int(step, "ms"),
            TimeoutMs = Int(step, "timeoutMs"),
            Handle = Str(step, "handle"),
            Button = Str(step, "button"),
            Control = Str(step, "control"),
            NoDialog = step.TryGetProperty("noDialog", out var nd) && nd.ValueKind == JsonValueKind.True,
            SettleMs = Int(step, "settleMs"),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
}

/// <summary>Decisions that depend on the shape of the batch, not on the app. Pure.</summary>
public static class BatchPlan
{
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The step after <paramref name="index"/> waits for a dialog, so a dialog here is expected.</summary>
    public static bool ExpectsDialog(IReadOnlyList<BatchStep> steps, int index) =>
        index + 1 < steps.Count && steps[index + 1].IsWaitFor(WaitConditions.DialogOpen);

    /// <summary>
    /// How long a click keeps watching for a dialog after it returns. Null means the default (250ms).
    /// Skipped when the step says it opens none, or when the next step waits for the dialog anyway.
    /// </summary>
    public static TimeSpan? SettleTime(IReadOnlyList<BatchStep> steps, int index)
    {
        var step = steps[index];
        if (step.SettleMs is >= 0) return TimeSpan.FromMilliseconds(Math.Min(step.SettleMs.Value, 5_000));
        if (step.NoDialog || ExpectsDialog(steps, index)) return TimeSpan.Zero;
        return null;
    }

    /// <summary>
    /// A click that opened a dialog (or is still running) stops the batch, unless the next step
    /// expects a dialog.
    /// </summary>
    public static bool StopAfterClick(bool dialogOpened, bool stillRunning, IReadOnlyList<BatchStep> steps, int index) =>
        (dialogOpened || stillRunning) && !ExpectsDialog(steps, index);

    /// <summary>Wait timeout for a step, clamped to what's left of the batch's time budget.</summary>
    public static TimeSpan WaitTimeout(BatchStep step, TimeSpan remaining)
    {
        var requested = step.TimeoutMs is > 0 ? TimeSpan.FromMilliseconds(step.TimeoutMs.Value) : DefaultWaitTimeout;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return requested < remaining ? requested : remaining;
    }

    public static bool TextMatches(string? actual, string expected) =>
        actual != null && actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
}
