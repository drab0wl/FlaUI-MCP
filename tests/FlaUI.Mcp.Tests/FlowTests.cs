using System.Text.Json.Nodes;
using PlaywrightWindows.Mcp.Core.Flows;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class FlowTests
{
    private static JsonArray A(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public void Placeholders_AndApply()
    {
        var actions = A("""[{"action":"menu","path":["File","Recent","{{file}}"]},{"action":"wait","until":"element","nameContains":"{{ file }} - {{app}}"}]""");
        Assert.Equal(new[] { "file", "app" }, FlowFormat.Placeholders(actions));

        var applied = FlowFormat.Apply(actions, new Dictionary<string, string> { ["file"] = "Report.txt", ["app"] = "Editor" }, out var error);
        Assert.Null(error);
        Assert.Equal("Report.txt", applied![0]!["path"]![2]!.GetValue<string>());
        Assert.Equal("Report.txt - Editor", applied[1]!["nameContains"]!.GetValue<string>());
        // The original is untouched.
        Assert.Equal("{{file}}", actions[0]!["path"]![2]!.GetValue<string>());

        Assert.Null(FlowFormat.Apply(actions, new Dictionary<string, string> { ["file"] = "x" }, out error));
        Assert.Equal("Missing argument: app.", error);
    }

    [Fact]
    public void ToPortable_RewritesRefsAndDropsHandles()
    {
        var actions = A("""
            [
              {"action":"click","ref":"w1e5","noDialog":true},
              {"action":"fill","ref":"w1e9","value":"{{name}}"},
              {"action":"click","selector":{"name":"OK","handle":"w7"}},
              {"action":"dialog_press","handle":"$dialog","button":"yes"},
              {"action":"wait","ms":500},
              {"action":"snapshot","handle":"w1"}
            ]
            """);
        var refs = new Dictionary<string, RefInfo>
        {
            ["w1e5"] = new("Save", "btnSave", "button"),
            ["w1e9"] = new("Name", null, "textbox"),
        };
        var warnings = new List<string>();

        var portable = FlowFormat.ToPortable(actions, r => refs.GetValueOrDefault(r), warnings, out var error);

        Assert.Null(error);
        Assert.Equal("""{"action":"click","noDialog":true,"automationId":"btnSave","role":"button"}""", portable![0]!.ToJsonString());
        Assert.Equal("""{"action":"fill","value":"{{name}}","name":"Name","role":"textbox"}""", portable[1]!.ToJsonString());
        Assert.Equal("""{"action":"click","selector":{"name":"OK"}}""", portable[2]!.ToJsonString());
        Assert.Equal("$dialog", portable[3]!["handle"]!.GetValue<string>());
        Assert.Equal("""{"action":"snapshot"}""", portable[5]!.ToJsonString());
        Assert.Contains(warnings, w => w.Contains("dropped handle w7"));
        Assert.Contains(warnings, w => w.Contains("Action 5 waits a fixed time"));
    }

    [Fact]
    public void ToPortable_FailsOnRefsItCannotDescribe()
    {
        var warnings = new List<string>();
        Assert.Null(FlowFormat.ToPortable(A("""[{"action":"click","ref":"w1e1"}]"""), _ => null, warnings, out var error));
        Assert.Contains("no longer exists", error);

        Assert.Null(FlowFormat.ToPortable(A("""[{"action":"click","ref":"w1e1"}]"""), _ => new RefInfo(null, "", "group"), warnings, out error));
        Assert.Contains("no name or automation id", error);
    }

    [Theory]
    [InlineData("open-recent", true)]
    [InlineData("build_2", true)]
    [InlineData("../evil", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    public void Names(string name, bool valid)
    {
        Assert.Equal(valid, FlowFormat.IsValidName(name));
    }

    [Fact]
    public void Store_SavesListsAndLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-flow-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FlowStore(dir);
            Assert.Empty(store.List());

            store.Save("open-recent", new JsonObject
            {
                ["description"] = "Open a recent file",
                ["params"] = new JsonObject { ["file"] = "File name" },
                ["actions"] = A("""[{"action":"menu","path":["File","{{file}}"]}]"""),
            });
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ nope");

            var list = store.List();
            Assert.Equal(new[] { "broken", "open-recent" }, list.Select(f => f.Name));
            Assert.Equal("open-recent(file: File name) - Open a recent file [1 steps]", FlowStore.Describe(list[1]));
            Assert.Equal("Open a recent file", store.Load("open-recent")!["description"]!.GetValue<string>());
            Assert.Null(store.Load("../open-recent"));
            Assert.Throws<ArgumentException>(() => store.Save("../x", new JsonObject()));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parameterize_TurnsValuesIntoPlaceholders_LongestFirst()
    {
        var actions = A("""[{"action":"menu","path":["File","Recent","Report.txt"]},{"action":"wait","until":"element","nameContains":"Report.txt - Editor"},{"action":"fill","name":"Name","value":"Report"}]""");

        var result = FlowFormat.Parameterize(actions, new Dictionary<string, string> { ["file"] = "Report.txt", ["base"] = "Report", ["empty"] = "" });

        Assert.Equal("{{file}}", result[0]!["path"]![2]!.GetValue<string>());
        Assert.Equal("{{file}} - Editor", result[1]!["nameContains"]!.GetValue<string>());
        Assert.Equal("{{base}}", result[2]!["value"]!.GetValue<string>());
        Assert.Equal(new[] { "file", "base" }, FlowFormat.Placeholders(result));

        // Round trip.
        var applied = FlowFormat.Apply(result, new Dictionary<string, string> { ["file"] = "Notes.md", ["base"] = "Notes" }, out _);
        Assert.Equal("Notes.md - Editor", applied![1]!["nameContains"]!.GetValue<string>());
    }
}
