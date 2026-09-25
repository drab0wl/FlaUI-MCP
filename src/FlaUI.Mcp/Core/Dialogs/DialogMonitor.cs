using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core.Dialogs;

/// <summary>
/// Finds dialogs by enumerating top-level windows with Win32. Cheap enough to call every
/// 50 ms, and never touches UI Automation, so it works while an app's UIA provider is
/// blocked inside a modal dialog's message loop.
/// </summary>
public sealed class DialogMonitor
{
    public IReadOnlyList<TopLevelWindowInfo> EnumerateTopLevel(IReadOnlySet<int>? processIds = null)
    {
        var windows = new List<TopLevelWindowInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var pid = NativeMethods.GetProcessId(hwnd);
            if (processIds != null && !processIds.Contains(pid)) return true;

            var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
            windows.Add(new TopLevelWindowInfo(
                hwnd,
                pid,
                owner,
                NativeMethods.GetClassName(hwnd),
                NativeMethods.GetCaption(hwnd),
                NativeMethods.IsWindowVisible(hwnd),
                NativeMethods.IsWindowEnabled(hwnd),
                owner == 0 || NativeMethods.IsWindowEnabled(owner)));
            return true;
        }, 0);
        return windows;
    }

    public IReadOnlyList<DialogInfo> GetDialogs(IReadOnlySet<int>? processIds)
    {
        return DialogClassifier.FindDialogs(EnumerateTopLevel(processIds), processIds);
    }

    /// <summary>True while the window exists and is visible.</summary>
    public static bool IsOpen(nint hwnd) => NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd);

    /// <summary>Describe an arbitrary top-level window, or null if it's gone.</summary>
    public static DialogInfo? Describe(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return null;
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        return new DialogInfo(
            hwnd,
            NativeMethods.GetProcessId(hwnd),
            owner,
            NativeMethods.GetClassName(hwnd),
            NativeMethods.GetCaption(hwnd),
            owner != 0 && !NativeMethods.IsWindowEnabled(owner),
            NativeMethods.IsWindowEnabled(hwnd));
    }
}
