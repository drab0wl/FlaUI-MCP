using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlaywrightWindows.Mcp.Core.Flows;

/// <summary>What a ref pointed at, for rewriting it as a selector.</summary>
public sealed record RefInfo(string? Name, string? AutomationId, string Role);

public sealed record FlowSummary(string Name, string Description, IReadOnlyDictionary<string, string> Params, int Steps);

/// <summary>
/// Saved batches: JSON files of windows_batch actions with {{param}} placeholders. Refs and
/// window handles only mean something in one session, so saving rewrites them as selectors.
/// </summary>
public static class FlowFormat
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex WindowHandle = new(@"^w\d+$", RegexOptions.Compiled);
    private static readonly Regex ValidName = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    public static bool IsValidName(string? name) => name != null && ValidName.IsMatch(name);

    /// <summary>Placeholder names used anywhere in the actions, in order of first use.</summary>
    public static IReadOnlyList<string> Placeholders(JsonNode? node)
    {
        var names = new List<string>();
        Visit(node, s =>
        {
            foreach (Match m in Placeholder.Matches(s))
            {
                if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
            }
            return s;
        });
        return names;
    }

    /// <summary>
    /// Turns the values a batch ran with into placeholders: with {"file": "Report.txt"}, every
    /// "Report.txt" inside a string becomes "{{file}}". Longer values are replaced first.
    /// </summary>
    public static JsonArray Parameterize(JsonArray actions, IReadOnlyDictionary<string, string> values)
    {
        var copy = (JsonArray)actions.DeepClone();
        var ordered = values.Where(v => v.Value.Length > 0).OrderByDescending(v => v.Value.Length).ToList();
        Visit(copy, s =>
        {
            foreach (var (name, value) in ordered) s = s.Replace(value, "{{" + name + "}}", StringComparison.Ordinal);
            return s;
        });
        return copy;
    }

    /// <summary>A copy of the actions with {{param}} replaced by the arguments.</summary>
    public static JsonArray? Apply(JsonArray actions, IReadOnlyDictionary<string, string> args, out string? error)
    {
        var missing = Placeholders(actions).Where(p => !args.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            error = $"Missing argument{(missing.Count > 1 ? "s" : "")}: {string.Join(", ", missing)}.";
            return null;
        }
        error = null;
        var copy = (JsonArray)actions.DeepClone();
        Visit(copy, s => Placeholder.Replace(s, m => args[m.Groups[1].Value]));
        return copy;
    }

    /// <summary>
    /// Rewrites session-bound parts of batch actions: a ref becomes automationId (or name) +
    /// role; window handles ("w3") are dropped so the flow searches the app's windows.
    /// </summary>
    /// <param name="resolveRef">What a ref points at, or null if it's gone.</param>
    public static JsonArray? ToPortable(JsonArray actions, Func<string, RefInfo?> resolveRef, List<string> warnings, out string? error)
    {
        error = null;
        var result = new JsonArray();
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not JsonObject original)
            {
                error = $"Action {i + 1} isn't an object.";
                return null;
            }
            var step = (JsonObject)original.DeepClone();
            var action = step["action"]?.GetValue<string>() ?? "";

            if (step["ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var refId))
            {
                var info = resolveRef(refId);
                if (info == null)
                {
                    error = $"Action {i + 1} uses {refId}, which no longer exists. Save right after the batch runs, or use a selector.";
                    return null;
                }
                step.Remove("ref");
                if (!string.IsNullOrWhiteSpace(info.AutomationId) && info.AutomationId.Length <= 80)
                {
                    step["automationId"] = info.AutomationId;
                }
                else if (!string.IsNullOrWhiteSpace(info.Name))
                {
                    step["name"] = info.Name;
                }
                else
                {
                    error = $"Action {i + 1}: {refId} has no name or automation id to find it by next time. Use a selector.";
                    return null;
                }
                step["role"] = info.Role;
            }

            DropHandle(step, i, warnings);
            if (step["selector"] is JsonObject selector) DropHandle(selector, i, warnings);

            if (action == "wait" && step.ContainsKey("ms") && !step.ContainsKey("until"))
            {
                warnings.Add($"Action {i + 1} waits a fixed time; \"until\" conditions hold up better when the app is slower or faster.");
            }
            result.Add(step);
        }
        return result;
    }

    private static void DropHandle(JsonObject step, int index, List<string> warnings)
    {
        if (step["handle"] is JsonValue h && h.TryGetValue<string>(out var handle) && WindowHandle.IsMatch(handle))
        {
            step.Remove("handle");
            warnings.Add($"Action {index + 1}: dropped handle {handle} (handles don't outlast the session); the flow searches the app's windows.");
        }
    }

    /// <summary>Applies <paramref name="map"/> to every string value in the tree (in place).</summary>
    private static void Visit(JsonNode? node, Func<string, string> map)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s)) obj[key] = map(s);
                    else Visit(obj[key], map);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)) arr[i] = map(s);
                    else Visit(arr[i], map);
                }
                break;
        }
    }
}

/// <summary>Flows as files in one folder: &lt;name&gt;.json.</summary>
public sealed class FlowStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public FlowStore(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "flaui-mcp", "flows");

    public string PathFor(string name) => Path.Combine(Directory, name + ".json");

    public bool Exists(string name) => File.Exists(PathFor(name));

    public void Save(string name, JsonObject flow)
    {
        if (!FlowFormat.IsValidName(name)) throw new ArgumentException($"Invalid flow name \"{name}\": letters, digits, - and _ only.");
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, flow.ToJsonString(Pretty));
        File.Move(temp, path, overwrite: true);
    }

    public JsonObject? Load(string name)
    {
        if (!FlowFormat.IsValidName(name) || !Exists(name)) return null;
        return JsonNode.Parse(File.ReadAllText(PathFor(name))) as JsonObject;
    }

    public IReadOnlyList<FlowSummary> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<FlowSummary>();
        var result = new List<FlowSummary>();
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!FlowFormat.IsValidName(name)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject flow) continue;
                result.Add(Summarize(name, flow));
            }
            catch (JsonException)
            {
                result.Add(new FlowSummary(name, "(unreadable JSON)", new Dictionary<string, string>(), 0));
            }
        }
        return result;
    }

    public static FlowSummary Summarize(string name, JsonObject flow)
    {
        var parameters = new Dictionary<string, string>();
        if (flow["params"] is JsonObject p)
        {
            foreach (var (key, value) in p) parameters[key] = value is JsonValue v && v.TryGetValue<string>(out var d) ? d : "";
        }
        return new FlowSummary(
            name,
            flow["description"] is JsonValue dv && dv.TryGetValue<string>(out var description) ? description : "",
            parameters,
            (flow["actions"] as JsonArray)?.Count ?? 0);
    }

    public static string Describe(FlowSummary f) =>
        $"{f.Name}{(f.Params.Count > 0 ? "(" + string.Join(", ", f.Params.Select(p => p.Value.Length > 0 ? $"{p.Key}: {p.Value}" : p.Key)) + ")" : "")}" +
        $" - {(f.Description.Length > 0 ? f.Description : "no description")} [{f.Steps} steps]";
}
