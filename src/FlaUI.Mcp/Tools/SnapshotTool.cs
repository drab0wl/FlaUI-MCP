using System.Text.Json;
using FlaUI.Core.AutomationElements;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Snapshots;
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
        "understanding window contents - use it before interacting with elements. On big windows, pass " +
        "ref to read one part (e.g. a pane found with windows_find), depth to stop early, or compact=true " +
        "to hide offscreen elements and layout-only groups.";

    /// <summary>FLAUI_MCP_SNAPSHOT_COMPACT=1 makes compact the default.</summary>
    public static bool CompactByDefault { get; } =
        Environment.GetEnvironmentVariable("FLAUI_MCP_SNAPSHOT_COMPACT")?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle from windows_launch or windows_list_windows. If omitted, uses the most recently launched window."
            },
            @ref = new
            {
                type = "string",
                description = "Snapshot only this element and what's inside it (refs elsewhere in the window stay valid)"
            },
            depth = new
            {
                type = "integer",
                description = "Levels below the root to include (default 10)"
            },
            compact = new
            {
                type = "boolean",
                description = "Hide offscreen elements and unnamed groups that wrap a single element (default false)"
            }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var refId = GetStringArgument(arguments, "ref");
        var depth = GetArgument<int?>(arguments, "depth");
        var compact = GetArgument<bool?>(arguments, "compact") ?? CompactByDefault;
        var limits = SnapshotLimits.Unbounded with { MaxDepth = depth is >= 0 ? depth.Value : 10 };

        if (!string.IsNullOrEmpty(refId))
        {
            return Task.FromResult(SnapshotSubtree(refId, limits, compact));
        }

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

            var snapshot = _snapshotBuilder.Build(handle!, window, limits).Text;
            return Task.FromResult(TextResult(compact ? SnapshotText.CompactText(snapshot) : snapshot));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture snapshot: {ex.Message}"));
        }
    }

    private McpToolResult SnapshotSubtree(string refId, SnapshotLimits limits, bool compact)
    {
        var handle = ElementRegistry.WindowHandleOf(refId);
        var element = _elementRegistry.GetElement(refId);
        if (handle == null || element == null)
        {
            return ErrorResult($"Element not found: {refId}. Run windows_snapshot or windows_find to refresh element refs.");
        }

        var blocked = CheckBlocked(handle);
        if (blocked != null) return ErrorResult(blocked);

        try
        {
            var snapshot = _snapshotBuilder.Build(handle, element, limits, partial: true).Text;
            return TextResult(compact ? SnapshotText.CompactText(snapshot) : snapshot);
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed to capture snapshot: {ex.Message}");
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
