using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using PlaywrightWindows.Mcp.Core.Win32;
using FlaUIApplication = FlaUI.Core.Application;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Manages UI Automation sessions and launched applications
/// </summary>
public class SessionManager : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Dictionary<string, FlaUIApplication> _applications = new();
    private readonly Dictionary<string, Window> _windows = new();
    // Handles registered from a raw HWND (e.g. a dialog found by the Win32 monitor) and
    // resolved to a UIA Window only when a UIA tool needs it.
    private readonly Dictionary<string, nint> _handleToHwnd = new();
    private readonly Dictionary<nint, string> _hwndToHandle = new();
    private readonly HashSet<int> _trackedProcessIds = new();
    private readonly object _gate = new();
    private int _windowCounter = 0;

    public SessionManager()
    {
        _automation = new UIA3Automation();

        // Opt-in: make UIA calls against a blocked provider fail fast instead of waiting the
        // UIA default (20s). Off by default because it also caps legitimately slow calls.
        var timeout = Environment.GetEnvironmentVariable("FLAUI_MCP_UIA_TRANSACTION_TIMEOUT_MS");
        if (int.TryParse(timeout, out var ms) && ms > 0)
        {
            _automation.TransactionTimeout = TimeSpan.FromMilliseconds(ms);
        }
    }

    public UIA3Automation Automation => _automation;

    public (string handle, Window window) LaunchApp(string appPath, string[]? args = null)
    {
        // Use Process.Start for more reliable launching
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appPath,
            Arguments = args != null ? string.Join(" ", args) : "",
            UseShellExecute = true
        };
        
        var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new Exception($"Failed to start process: {appPath}");
        }
        
        // Wait for the process to be ready
        try
        {
            process.WaitForInputIdle(5000);
        }
        catch { /* Some processes don't support this */ }
        
        Thread.Sleep(1000); // Extra wait for window to appear
        
        // Find window by process ID from desktop
        var desktop = _automation.GetDesktop();
        Window? window = null;
        
        // Try to find by process ID first
        var element = desktop.FindFirstDescendant(cf => cf.ByProcessId(process.Id));
        if (element != null)
        {
            window = element.AsWindow();
        }
        
        // If not found, the app might have spawned a different process (common for UWP)
        // Search by waiting for a new window
        if (window == null)
        {
            // Get window count before
            var existingTitles = new HashSet<string>(
                _windows.Values.Select(w => w.Title).Where(t => !string.IsNullOrEmpty(t))
            );
            
            // Wait and look for new windows
            for (int i = 0; i < 10 && window == null; i++)
            {
                Thread.Sleep(500);
                var windows = desktop.FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
                foreach (var w in windows)
                {
                    var win = w.AsWindow();
                    if (win != null && !string.IsNullOrEmpty(win.Title))
                    {
                        // Check if this looks like our app
                        var title = win.Title.ToLowerInvariant();
                        var appName = Path.GetFileNameWithoutExtension(appPath).ToLowerInvariant();
                        if (title.Contains(appName) || !existingTitles.Contains(win.Title))
                        {
                            window = win;
                            break;
                        }
                    }
                }
            }
        }
        
        if (window == null)
        {
            throw new Exception($"Could not find window for {appPath}. Try using windows_list_windows and windows_focus instead.");
        }

        var windowHandle = RegisterWindow(window);
        return (windowHandle, window);
    }

    public (string handle, Window window) AttachToWindow(string title)
    {
        var desktop = _automation.GetDesktop();
        var window = desktop.FindFirstDescendant(cf => cf.ByName(title))?.AsWindow();
        
        if (window == null)
        {
            throw new Exception($"Window not found: {title}");
        }

        var handle = RegisterWindow(window);
        return (handle, window);
    }

    /// <param name="track">
    /// Report this window's process's dialogs in tool results. False for bulk listings, so
    /// listing the desktop doesn't start reporting every app's dialogs.
    /// </param>
    public string RegisterWindow(Window window, bool track = true)
    {
        nint hwnd = 0;
        int pid = 0;
        try { hwnd = window.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        try { pid = window.Properties.ProcessId.ValueOrDefault; } catch { }

        lock (_gate)
        {
            if (track && pid != 0) _trackedProcessIds.Add(pid);

            // Same HWND -> same handle, so repeated list/snapshot calls don't mint new handles.
            if (hwnd != 0 && _hwndToHandle.TryGetValue(hwnd, out var existing))
            {
                _windows[existing] = window;
                return existing;
            }

            var handle = $"w{++_windowCounter}";
            _windows[handle] = window;
            if (hwnd != 0)
            {
                _hwndToHandle[hwnd] = handle;
                _handleToHwnd[handle] = hwnd;
            }
            return handle;
        }
    }

    /// <summary>
    /// Register a top-level window by HWND without touching UI Automation. The UIA element is
    /// created on first use, so this is safe to call while the app's UIA provider is blocked.
    /// </summary>
    public string RegisterNativeWindow(nint hwnd, int processId, bool track = true)
    {
        lock (_gate)
        {
            if (track && processId != 0) _trackedProcessIds.Add(processId);
            if (_hwndToHandle.TryGetValue(hwnd, out var existing)) return existing;

            var handle = $"w{++_windowCounter}";
            _hwndToHandle[hwnd] = handle;
            _handleToHwnd[handle] = hwnd;
            return handle;
        }
    }

    public Window? GetWindow(string handle)
    {
        nint hwnd;
        lock (_gate)
        {
            _handleToHwnd.TryGetValue(handle, out hwnd);
            if (hwnd != 0)
            {
                // Using a handle means the agent is working with that app: report its dialogs.
                var pid = NativeMethods.GetProcessId(hwnd);
                if (pid != 0) _trackedProcessIds.Add(pid);
            }
            if (_windows.TryGetValue(handle, out var window)) return window;
            if (hwnd == 0) return null;
        }

        if (!NativeMethods.IsWindow(hwnd)) return null;
        var resolved = _automation.FromHandle(hwnd).AsWindow();
        if (resolved == null) return null;
        lock (_gate) _windows[handle] = resolved;
        return resolved;
    }

    /// <summary>The HWND behind a handle, if known. Never calls UI Automation.</summary>
    public nint GetHwnd(string handle)
    {
        lock (_gate)
        {
            return _handleToHwnd.TryGetValue(handle, out var hwnd) ? hwnd : 0;
        }
    }

    /// <summary>Remember a process so its dialogs are reported in tool results.</summary>
    public void TrackProcess(int processId)
    {
        if (processId == 0) return;
        lock (_gate) _trackedProcessIds.Add(processId);
    }

    public IReadOnlySet<int> TrackedProcessIds
    {
        get
        {
            lock (_gate) return new HashSet<int>(_trackedProcessIds);
        }
    }

    public List<(string handle, string title, string? processName)> ListWindows()
    {
        var desktop = _automation.GetDesktop();
        var windows = desktop.FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
        
        var result = new List<(string, string, string?)>();
        foreach (var w in windows)
        {
            var window = w.AsWindow();
            if (window != null && !string.IsNullOrEmpty(window.Title))
            {
                var handle = RegisterWindow(window, track: false);
                string? processName = null;
                try 
                { 
                    processName = window.Properties.ProcessId.TryGetValue(out var pid) 
                        ? System.Diagnostics.Process.GetProcessById(pid).ProcessName 
                        : null; 
                }
                catch { }
                
                result.Add((handle, window.Title, processName));
            }
        }
        return result;
    }

    public void FocusWindow(string handle)
    {
        var window = GetWindow(handle);
        if (window == null)
        {
            throw new Exception($"Window not found: {handle}");
        }
        window.Focus();
    }

    public void CloseWindow(string handle)
    {
        var window = GetWindow(handle);
        if (window == null)
        {
            throw new Exception($"Window not found: {handle}");
        }
        window.Close();
        lock (_gate)
        {
            _windows.Remove(handle);
            if (_handleToHwnd.Remove(handle, out var hwnd)) _hwndToHandle.Remove(hwnd);
        }
    }

    public void Dispose()
    {
        foreach (var app in _applications.Values)
        {
            try { app.Close(); } catch { }
        }
        _applications.Clear();
        lock (_gate)
        {
            _windows.Clear();
            _handleToHwnd.Clear();
            _hwndToHandle.Clear();
        }
        _automation.Dispose();
    }
}
