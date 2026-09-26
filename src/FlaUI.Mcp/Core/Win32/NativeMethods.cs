using System.Runtime.InteropServices;
using System.Text;

namespace PlaywrightWindows.Mcp.Core.Win32;

/// <summary>
/// Win32 calls used for dialog detection and the UIA-free dialog fallback.
/// Nothing here goes through UI Automation, so it keeps working while a UIA provider is blocked.
/// Cross-process calls that send messages use SendMessageTimeout with SMTO_ABORTIFHUNG.
/// </summary>
internal static class NativeMethods
{
    public const uint GW_OWNER = 4;
    public const uint GA_ROOT = 2;
    public const int GWL_STYLE = -16;

    public const uint WM_NULL = 0x0000;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_COMMAND = 0x0111;
    public const uint BM_GETCHECK = 0x00F0;
    public const uint BM_CLICK = 0x00F5;
    public const int BN_CLICKED = 0;

    public const uint SMTO_BLOCK = 0x0001;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const int SW_RESTORE = 9;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern nint GetParent(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    // For a window in another process, GetWindowText returns the caption without sending
    // WM_GETTEXT, so it cannot block on a busy UI thread.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    // GetWindowLongPtrW is only exported by 64-bit user32.
    public static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, string lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public static string GetClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static string GetCaption(nint hwnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// Reads a control's text via WM_GETTEXT with a timeout (works for Edit contents, which
    /// GetWindowText cannot read cross-process). Returns null if the window didn't answer.
    /// </summary>
    public static string? GetTextWithTimeout(nint hwnd, uint timeoutMs = 500)
    {
        if (SendMessageTimeout(hwnd, WM_GETTEXTLENGTH, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out var len) == 0)
        {
            return null;
        }
        var capacity = (int)Math.Clamp((long)len + 1, 1, 32 * 1024);
        var sb = new StringBuilder(capacity);
        if (SendMessageTimeout(hwnd, WM_GETTEXT, capacity, sb, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out _) == 0)
        {
            return null;
        }
        return sb.ToString();
    }

    public static bool SetTextWithTimeout(nint hwnd, string text, uint timeoutMs = 1000)
    {
        return SendMessageTimeout(hwnd, WM_SETTEXT, 0, text, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out _) != 0;
    }

    public static int GetProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public static nint RootOf(nint hwnd) => hwnd == 0 ? 0 : GetAncestor(hwnd, GA_ROOT);

    /// <summary>
    /// Brings a top-level window to the foreground. Windows restricts SetForegroundWindow for
    /// background processes; attaching to the current foreground thread's input queue is the
    /// standard workaround. Returns whether the window ended up in the foreground.
    /// </summary>
    public static bool BringToForeground(nint hwnd)
    {
        if (hwnd == 0) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (GetForegroundWindow() == hwnd) return true;

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var attached = foregroundThread != 0 && foregroundThread != currentThread
                       && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }

        return GetForegroundWindow() == hwnd;
    }
}
