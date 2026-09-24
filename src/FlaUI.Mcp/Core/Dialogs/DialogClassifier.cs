namespace PlaywrightWindows.Mcp.Core.Dialogs;

/// <summary>
/// Raw facts about a top-level window, read with plain Win32 calls (never UI Automation),
/// so they can be collected even while an app's UIA provider is blocked.
/// </summary>
public sealed record TopLevelWindowInfo(
    nint Hwnd,
    int ProcessId,
    nint Owner,
    string ClassName,
    string Title,
    bool IsVisible,
    bool IsEnabled,
    bool OwnerEnabled);

/// <summary>
/// A top-level window that looks like a dialog.
/// </summary>
/// <param name="IsModal">The window's owner is disabled, i.e. the owner is blocked until this closes.</param>
/// <param name="IsEnabled">False when a nested dialog is blocking this one.</param>
public sealed record DialogInfo(
    nint Hwnd,
    int ProcessId,
    nint Owner,
    string ClassName,
    string Title,
    bool IsModal,
    bool IsEnabled)
{
    /// <summary>Classic Win32 dialog (MessageBox, common dialogs, most native dialogs).</summary>
    public bool IsWin32Dialog => string.Equals(ClassName, DialogClassifier.Win32DialogClass, StringComparison.Ordinal);

    /// <summary>Dialogs worth interrupting the agent about: modal ones and classic Win32 dialogs.</summary>
    public bool IsBlocking => IsModal || IsWin32Dialog;
}

/// <summary>
/// Decides which top-level windows are dialogs. Pure logic so it can be unit tested anywhere.
/// </summary>
public static class DialogClassifier
{
    public const string Win32DialogClass = "#32770";

    // Owned top-level windows that are never dialogs.
    private static readonly HashSet<string> NoiseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tooltips_class32",
        "#32768",            // menu
        "SysShadow",
        "IME",
        "Default IME",
        "MSCTFIME UI",
        "ForegroundStaging",
        "WorkerW",
        "Progman",
    };

    public static IReadOnlyList<DialogInfo> FindDialogs(
        IEnumerable<TopLevelWindowInfo> windows,
        IReadOnlySet<int>? processIds = null)
    {
        var result = new List<DialogInfo>();
        foreach (var w in windows)
        {
            if (!w.IsVisible) continue;
            if (processIds != null && !processIds.Contains(w.ProcessId)) continue;
            if (NoiseClasses.Contains(w.ClassName)) continue;

            var owned = w.Owner != 0;
            var isModal = owned && !w.OwnerEnabled;
            var isWin32Dialog = string.Equals(w.ClassName, Win32DialogClass, StringComparison.Ordinal);
            // Owned + titled covers WPF/WinForms dialogs that aren't modal (e.g. Show(owner)).
            // Untitled owned windows are usually popups, adorners, drop-downs.
            var isDialog = isModal || isWin32Dialog || (owned && !string.IsNullOrWhiteSpace(w.Title));
            if (!isDialog) continue;

            result.Add(new DialogInfo(w.Hwnd, w.ProcessId, w.Owner, w.ClassName, w.Title, isModal, w.IsEnabled));
        }
        return result;
    }

    /// <summary>Dialogs in <paramref name="current"/> that were not present in <paramref name="baseline"/>.</summary>
    public static IReadOnlyList<DialogInfo> NewSince(
        IEnumerable<DialogInfo> baseline,
        IEnumerable<DialogInfo> current)
    {
        var known = new HashSet<nint>(baseline.Select(d => d.Hwnd));
        return current.Where(d => !known.Contains(d.Hwnd)).ToList();
    }

    /// <summary>
    /// A short human-readable description, e.g. <c>"Confirm" [modal] [win32]</c>.
    /// </summary>
    public static string Describe(DialogInfo dialog)
    {
        var parts = new List<string> { $"\"{dialog.Title}\"" };
        if (dialog.IsModal) parts.Add("[modal]");
        if (dialog.IsWin32Dialog) parts.Add("[win32]");
        if (!dialog.IsEnabled) parts.Add("[blocked by another dialog]");
        return string.Join(" ", parts);
    }
}
