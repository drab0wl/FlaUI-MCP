using System.Text.Json;
using System.Text.Json.Nodes;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Flows;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>Run a batch saved with windows_batch saveAs, or list the saved ones.</summary>
public class RunFlowTool : ToolBase
{
    private readonly FlowStore _store;
    private readonly BatchTool _batch;

    public RunFlowTool(FlowStore store, BatchTool batch)
    {
        _store = store;
        _batch = batch;
    }

    public override string Name => "windows_run_flow";

    public override string Description =>
        "Run a saved flow (a windows_batch saved with saveAs) in one call, filling its parameters from args. " +
        "Without a name, lists the saved flows and their parameters; show=true prints a flow's steps. " +
        "Results are the same as windows_batch.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "Flow to run; omit to list them" },
            args = new { type = "object", description = "Values for the flow's parameters, e.g. {\"file\": \"Report.txt\"}" },
            show = new { type = "boolean", description = "Print the flow's steps instead of running it" },
            postSnapshot = new { type = new[] { "boolean", "string" }, description = PostActionModes.SchemaDescription }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var name = GetStringArgument(arguments, "name");
        if (string.IsNullOrWhiteSpace(name)) return TextResult(List());

        var flow = _store.Load(name);
        if (flow == null) return ErrorResult($"No flow \"{name}\". {List()}");

        if (GetBoolArgument(arguments, "show", false))
        {
            return TextResult(flow.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        if (flow["actions"] is not JsonArray actions) return ErrorResult($"Flow \"{name}\" has no actions list.");

        var args = new Dictionary<string, string>();
        if (arguments is { } a && a.TryGetProperty("args", out var given) && given.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in given.EnumerateObject())
            {
                args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
        }

        var filled = FlowFormat.Apply(actions, args, out var error);
        if (filled == null)
        {
            return ErrorResult($"{error} {FlowStore.Describe(FlowStore.Summarize(name, flow))}");
        }

        var batchArgs = new JsonObject { ["actions"] = filled };
        if (flow["onDialog"] is { } onDialog) batchArgs["onDialog"] = onDialog.DeepClone();
        if (arguments is { } b && b.TryGetProperty("postSnapshot", out var post))
        {
            batchArgs["postSnapshot"] = JsonNode.Parse(post.GetRawText());
        }

        var result = await _batch.ExecuteAsync(JsonSerializer.SerializeToElement(batchArgs));
        var content = result.Content.ToList();
        var first = content.FindIndex(c => c.Type == "text");
        if (first >= 0) content[first] = content[first] with { Text = $"Flow {name}:\n{content[first].Text}" };
        return result with { Content = content };
    }

    private string List()
    {
        var flows = _store.List();
        if (flows.Count == 0)
        {
            return $"No saved flows yet (folder: {_store.Directory}). Add saveAs to a windows_batch that worked to save one.";
        }
        return "Saved flows:\n" + string.Join("\n", flows.Select(f => "- " + FlowStore.Describe(f)));
    }
}
