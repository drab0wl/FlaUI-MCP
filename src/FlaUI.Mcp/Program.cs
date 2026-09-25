using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Tools;

DpiUtility.EnablePerMonitorV2();

if (args.Length > 0 && args[0] == "--bench-snapshot")
{
    return SnapshotBenchmark.Run(args.Skip(1).ToArray());
}

// Create shared services
var sessionManager = new SessionManager();
var elementRegistry = new ElementRegistry();
var dialogMonitor = new DialogMonitor();
var pendingOperations = new PendingOperationRegistry();
var actionRunner = new ActionRunner(pendingOperations);
var clickExecutor = new ClickExecutor(sessionManager, dialogMonitor, actionRunner, pendingOperations);
var postAction = new PostActionSnapshotter(sessionManager, elementRegistry, dialogMonitor, pendingOperations);

// Every tool result reports open dialogs and clicks that finished in the background.
var annotator = new SessionStatusAnnotator(sessionManager, dialogMonitor, pendingOperations);

// Register all tools
var toolRegistry = new ToolRegistry(annotator: annotator);
toolRegistry.RegisterTool(new LaunchTool(sessionManager));
toolRegistry.RegisterTool(new SnapshotTool(sessionManager, elementRegistry, pendingOperations));
toolRegistry.RegisterTool(new ClickTool(elementRegistry, clickExecutor, postAction));
toolRegistry.RegisterTool(new TypeTool(elementRegistry));
toolRegistry.RegisterTool(new FillTool(elementRegistry, postAction));
toolRegistry.RegisterTool(new GetTextTool(elementRegistry));
toolRegistry.RegisterTool(new SendKeysTool(elementRegistry));
toolRegistry.RegisterTool(new ScreenshotTool(sessionManager, elementRegistry));
toolRegistry.RegisterTool(new ListWindowsTool(sessionManager));
toolRegistry.RegisterTool(new FocusWindowTool(sessionManager));
toolRegistry.RegisterTool(new CloseWindowTool(sessionManager));
toolRegistry.RegisterTool(new BatchTool(sessionManager, elementRegistry, clickExecutor, dialogMonitor, pendingOperations, postAction));
toolRegistry.RegisterTool(new DialogsTool(sessionManager, dialogMonitor, pendingOperations));
toolRegistry.RegisterTool(new NativeDialogTool(sessionManager, postAction));
toolRegistry.RegisterTool(new WaitTool(sessionManager, dialogMonitor, pendingOperations));

// Create and run MCP server
var server = new McpServer(toolRegistry);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token);
}
finally
{
    TimingLog.Shared.WriteSummary();
    sessionManager.Dispose();
}

return 0;
