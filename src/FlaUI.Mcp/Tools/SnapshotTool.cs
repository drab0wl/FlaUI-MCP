using System.Text.Json;
using FlaUI.Core.AutomationElements;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Take accessibility snapshot of a window - THE KEY TOOL FOR AGENTS
/// </summary>
public class SnapshotTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly PendingOperationRegistry? _pending;

    public SnapshotTool(SessionManager sessionManager, ElementRegistry elementRegistry, PendingOperationRegistry? pending = null)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _snapshotBuilder = new SnapshotBuilder(elementRegistry);
        _pending = pending;
    }

    public override string Name => "windows_snapshot";

    public override string Description => 
        "Capture accessibility snapshot of a window. Returns a structured tree with element refs " +
        "that can be used with windows_click, windows_type, etc. This is the primary tool for " +
        "understanding window contents - use it before interacting with elements.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle from windows_launch or windows_list_windows. If omitted, uses the most recently launched window."
            }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");

        try
        {
            FlaUI.Core.AutomationElements.Window? window = null;

            if (!string.IsNullOrEmpty(handle))
            {
                var blocked = CheckBlocked(handle);
                if (blocked != null)
                {
                    return Task.FromResult(ErrorResult(blocked));
                }

                window = _sessionManager.GetWindow(handle);
                if (window == null)
                {
                    return Task.FromResult(ErrorResult($"Window not found: {handle}"));
                }
            }
            else
            {
                // Get the foreground window
                var desktop = _sessionManager.Automation.GetDesktop();
                var focusedElement = _sessionManager.Automation.FocusedElement();
                
                if (focusedElement != null)
                {
                    // Walk up to find the window
                    var current = focusedElement;
                    while (current != null)
                    {
                        if (current.Properties.ControlType.ValueOrDefault == FlaUI.Core.Definitions.ControlType.Window)
                        {
                            window = current.AsWindow();
                            break;
                        }
                        current = current.Parent;
                    }
                }

                if (window == null)
                {
                    return Task.FromResult(ErrorResult("No window specified and no focused window found. Use windows_list_windows to see available windows."));
                }

                // Register this window
                handle = _sessionManager.RegisterWindow(window);
            }

            var snapshot = _snapshotBuilder.BuildSnapshot(handle!, window);
            return Task.FromResult(TextResult(snapshot));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture snapshot: {ex.Message}"));
        }
    }

    /// <summary>
    /// When a click in this app is still pending (usually stuck behind a modal dialog), check
    /// that UI Automation answers before walking the tree, so a blocked provider costs 1.5s
    /// instead of the full tool timeout.
    /// </summary>
    private string? CheckBlocked(string handle)
    {
        if (_pending == null) return null;
        var hwnd = _sessionManager.GetHwnd(handle);
        if (hwnd == 0) return null;
        var pid = NativeMethods.GetProcessId(hwnd);
        if (!_pending.HasRunningFor(pid)) return null;

        var answered = Responsiveness.Probe(() =>
        {
            var w = _sessionManager.GetWindow(handle);
            _ = w?.Properties.Name.ValueOrDefault;
        }, TimeSpan.FromMilliseconds(1500));

        return answered ? null
            : $"UI Automation for {handle} is not answering: a pending click is holding the app's provider. " +
              $"Use windows_dialog handle={handle} to read and press buttons with Win32 messages, " +
              "or windows_screenshot to see it.";
    }
}
