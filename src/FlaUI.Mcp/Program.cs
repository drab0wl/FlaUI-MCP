using PlaywrightWindows.Mcp;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Dialogs;
using PlaywrightWindows.Mcp.Tools;

DpiUtility.EnablePerMonitorV2();

// Create shared services
var sessionManager = new SessionManager();
var elementRegistry = new ElementRegistry();
var dialogMonitor = new DialogMonitor();
var pendingOperations = new PendingOperationRegistry();
var actionRunner = new ActionRunner(pendingOperations);
var clickExecutor = new ClickExecutor(sessionManager, dialogMonitor, actionRunner, pendingOperations);

// Every tool result reports open dialogs and clicks that finished in the background.
var annotator = new SessionStatusAnnotator(sessionManager, dialogMonitor, pendingOperations);

// Register all tools
var toolRegistry = new ToolRegistry(annotator: annotator);
toolRegistry.RegisterTool(new LaunchTool(sessionManager));
toolRegistry.RegisterTool(new SnapshotTool(sessionManager, elementRegistry, pendingOperations));
toolRegistry.RegisterTool(new ClickTool(elementRegistry, clickExecutor));
toolRegistry.RegisterTool(new TypeTool(elementRegistry));
toolRegistry.RegisterTool(new FillTool(elementRegistry));
toolRegistry.RegisterTool(new GetTextTool(elementRegistry));
toolRegistry.RegisterTool(new SendKeysTool(elementRegistry));
toolRegistry.RegisterTool(new ScreenshotTool(sessionManager, elementRegistry));
toolRegistry.RegisterTool(new ListWindowsTool(sessionManager));
toolRegistry.RegisterTool(new FocusWindowTool(sessionManager));
toolRegistry.RegisterTool(new CloseWindowTool(sessionManager));
toolRegistry.RegisterTool(new BatchTool(sessionManager, elementRegistry, clickExecutor));
toolRegistry.RegisterTool(new DialogsTool(sessionManager, dialogMonitor, pendingOperations));
toolRegistry.RegisterTool(new NativeDialogTool(sessionManager));
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
    sessionManager.Dispose();
}
