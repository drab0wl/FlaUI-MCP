using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Shared fixture that launches the WinForms and WPF test apps once per test collection.
/// Provides SessionManager and ElementRegistry for all tests to share.
/// Implements IAsyncLifetime for proper async setup/teardown.
/// </summary>
public class TestAppFixture : IAsyncLifetime
{
    private const int WindowPollIntervalMs = 250;
    private const int WindowPollTimeoutMs = 15000;

    public SessionManager Session { get; } = new();
    public ElementRegistry Elements { get; } = new();
    public DialogMonitor Dialogs { get; } = new();
    public PendingOperationRegistry Pending { get; } = new();
    public ClickExecutor Clicks { get; }

    public TestAppFixture()
    {
        Clicks = new ClickExecutor(Session, Dialogs, new ActionRunner(Pending), Pending);
    }

    public ClickTool CreateClickTool() => new(Elements, Clicks);

    /// <summary>Post-action snapshots on, whatever FLAUI_MCP_POST_SNAPSHOT says.</summary>
    public PostActionSnapshotter CreatePostAction() =>
        new(Session, Elements, Dialogs, Pending, new PostActionOptions { MaxNodes = 150, MaxChars = 8000 });

    public PlaywrightWindows.Mcp.Core.Batch.ElementFinder CreateFinder() => new(Session, Elements, Dialogs, Pending);

    public SetTool CreateSetTool() => new(Elements, CreateFinder(), new StateActions(Clicks), CreatePostAction());

    public MenuTool CreateMenuTool() =>
        new(Session, Elements, CreateFinder(), new MenuActions(Session, Elements, Clicks), CreatePostAction());

    public ReadTableTool CreateReadTableTool() => new(Elements, CreateFinder());

    public BatchTool CreateBatchTool(bool postSnapshot = true) =>
        new(Session, Elements, Clicks, Dialogs, Pending, postSnapshot ? CreatePostAction() : null);

    public string WinFormsHandle { get; private set; } = "";
    public string WpfHandle { get; private set; } = "";

    private Process? _winFormsProcess;
    private Process? _wpfProcess;

    public async Task InitializeAsync()
    {
        var winFormsPath = FindTestAppPath("WinFormsTestApp")
            ?? throw new Exception(
                "WinFormsTestApp not found. Build it first: dotnet build tests/TestApps/WinFormsTestApp");
        var wpfPath = FindTestAppPath("WpfTestApp")
            ?? throw new Exception(
                "WpfTestApp not found. Build it first: dotnet build tests/TestApps/WpfTestApp");

        _winFormsProcess = LaunchTestApp(winFormsPath);
        _wpfProcess = LaunchTestApp(wpfPath);
        var winFormsProcessId = _winFormsProcess.Id;
        var wpfProcessId = _wpfProcess.Id;

        // Poll for both windows to appear
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < WindowPollTimeoutMs
               && (WinFormsHandle == "" || WpfHandle == ""))
        {
            await Task.Delay(WindowPollIntervalMs);

            var desktop = Session.Automation.GetDesktop();
            var windows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));
            foreach (var w in windows)
            {
                var win = w.AsWindow();
                var processId = w.Properties.ProcessId.ValueOrDefault;
                if (win?.Title == "FlaUI-MCP Test App"
                    && processId == winFormsProcessId
                    && WinFormsHandle == "")
                {
                    WinFormsHandle = Session.RegisterWindow(win);
                }
                else if (win?.Title == "FlaUI-MCP WPF Test App"
                         && processId == wpfProcessId
                         && WpfHandle == "")
                {
                    WpfHandle = Session.RegisterWindow(win);
                }
            }
        }

        if (WinFormsHandle == "")
            throw new Exception("Timed out waiting for WinForms test app window.");
        if (WpfHandle == "")
            throw new Exception("Timed out waiting for WPF test app window.");
    }

    public Task DisposeAsync()
    {
        CloseProcess(_winFormsProcess);
        CloseProcess(_wpfProcess);
        Session.Dispose();
        return Task.CompletedTask;
    }

    private static Process LaunchTestApp(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch test app: {path}");
    }

    private static string? FindTestAppPath(string appName)
    {
        var baseDir = AppContext.BaseDirectory;

        // Walk up to find the repo root (contains src/)
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        if (dir == null) return null;

        var searchPaths = new[]
        {
            Path.Combine(dir.FullName, "tests", "TestApps", appName, "bin", "Debug", "net8.0-windows", $"{appName}.exe"),
            Path.Combine(dir.FullName, "tests", "TestApps", appName, "bin", "Release", "net8.0-windows", $"{appName}.exe"),
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Close any dialogs a failed test left open in the test apps, so later tests start clean.
    /// </summary>
    public void DismissDialogs()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var open = Dialogs.GetDialogs(Session.TrackedProcessIds);
            if (open.Count == 0) return;
            foreach (var d in open)
            {
                var controls = NativeDialog.ReadControls(d.Hwnd);
                var button = NativeDialog.FindButton(controls, "cancel") ?? NativeDialog.FindButton(controls, "no");
                if (button != null) NativeDialog.Press(button);
                else NativeDialog.RequestClose(d.Hwnd);
            }
            Thread.Sleep(300);
        }
    }

    public Window? GetWinFormsWindow() => Session.GetWindow(WinFormsHandle);
    public Window? GetWpfWindow() => Session.GetWindow(WpfHandle);

    /// <summary>
    /// Call an MCP tool with the given arguments and return the text result.
    /// Shared helper to avoid duplication across test classes.
    /// </summary>
    public async Task<string> CallTool(ToolBase tool, object args)
    {
        var json = JsonSerializer.Serialize(args, McpProtocol.JsonOptions);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        var result = await tool.ExecuteAsync(element);
        return result.Content.FirstOrDefault()?.Text ?? "";
    }

    /// <summary>
    /// Take a snapshot of a window and return the text.
    /// </summary>
    public string TakeSnapshot(string handle)
    {
        var builder = new SnapshotBuilder(Elements);
        var window = Session.GetWindow(handle)!;
        return builder.BuildSnapshot(handle, window);
    }

    /// <summary>
    /// Find an element ref by name in the snapshot of the given window.
    /// Takes a fresh snapshot each time — use the overload accepting a
    /// pre-built snapshot when multiple lookups are needed.
    /// </summary>
    public string? FindRefByName(string handle, string name)
    {
        var snapshot = TakeSnapshot(handle);
        return FindRefInSnapshot(snapshot, name);
    }

    /// <summary>
    /// Find an element ref by name in a pre-built snapshot string.
    /// Use this when making multiple lookups against the same snapshot.
    /// </summary>
    public static string? FindRefInSnapshot(string snapshot, string name)
    {
        foreach (var line in snapshot.Split('\n'))
        {
            if (line.Contains($"\"{name}\"") && line.Contains("[ref="))
            {
                var refStart = line.IndexOf("[ref=") + 5;
                var refEnd = line.IndexOf("]", refStart);
                return line[refStart..refEnd];
            }
        }
        return null;
    }

    private static void CloseProcess(Process? process)
    {
        if (process == null || process.HasExited) return;
        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
            }
        }
        catch
        {
            try { process.Kill(); } catch { }
        }
    }
}

/// <summary>
/// Collection definition so all test classes share the same fixture (same app instances).
/// </summary>
[CollectionDefinition("TestApps")]
public class TestAppCollection : ICollectionFixture<TestAppFixture>
{
}
