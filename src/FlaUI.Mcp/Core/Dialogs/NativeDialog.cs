using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Core.Dialogs;

public enum NativeControlKind
{
    Button,
    CheckBox,
    RadioButton,
    GroupBox,
    Text,
    Edit,
    ComboBox,
    Other,
}

public sealed record NativeControl(
    string Ref,
    nint Hwnd,
    nint Parent,
    int ControlId,
    NativeControlKind Kind,
    string ClassName,
    string Text,
    bool IsVisible,
    bool IsEnabled,
    bool IsDefault,
    bool? IsChecked);

/// <summary>
/// Reads and drives HWND-based dialogs (MessageBox, #32770 dialogs, WinForms forms) with
/// window messages only. This is the fallback for when UI Automation can't reach a dialog
/// because the app's provider is blocked. It can't see inside WPF or DirectUI content
/// (task dialogs), which have no child HWNDs; use windows_snapshot for those.
/// </summary>
public static class NativeDialog
{
    // Standard dialog command IDs (winuser.h), so callers can say "yes" even when the
    // button text is localized.
    private static readonly Dictionary<string, int> StandardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ok"] = 1,
        ["cancel"] = 2,
        ["abort"] = 3,
        ["retry"] = 4,
        ["ignore"] = 5,
        ["yes"] = 6,
        ["no"] = 7,
        ["close"] = 8,
        ["help"] = 9,
        ["tryagain"] = 10,
        ["continue"] = 11,
    };

    public static IReadOnlyList<NativeControl> ReadControls(nint dialog)
    {
        var controls = new List<NativeControl>();
        NativeMethods.EnumChildWindows(dialog, (hwnd, _) =>
        {
            var cls = NativeMethods.GetClassName(hwnd);
            var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
            var kind = Classify(cls, style);
            var visible = NativeMethods.IsWindowVisible(hwnd);

            string text;
            if (kind is NativeControlKind.Edit or NativeControlKind.ComboBox)
            {
                text = NativeMethods.GetTextWithTimeout(hwnd) ?? "";
            }
            else
            {
                text = NativeMethods.GetCaption(hwnd);
                if (text.Length == 0) text = NativeMethods.GetTextWithTimeout(hwnd) ?? "";
            }

            bool? isChecked = null;
            if (kind is NativeControlKind.CheckBox or NativeControlKind.RadioButton
                && NativeMethods.SendMessageTimeout(hwnd, NativeMethods.BM_GETCHECK, 0, 0,
                    NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, 500, out var check) != 0)
            {
                isChecked = check == 1;
            }

            var buttonType = style & 0x0F;
            controls.Add(new NativeControl(
                $"c{controls.Count + 1}",
                hwnd,
                NativeMethods.GetParent(hwnd),
                NativeMethods.GetDlgCtrlID(hwnd),
                kind,
                cls,
                text,
                visible,
                NativeMethods.IsWindowEnabled(hwnd),
                kind == NativeControlKind.Button && (buttonType == 0x1 || buttonType == 0xD || buttonType == 0xF),
                isChecked));
            return true;
        }, 0);
        return controls;
    }

    /// <summary>
    /// Finds a button by visible text (ampersands ignored, case-insensitive; exact match first,
    /// then prefix), by control ref (<c>c3</c>), or by standard name (<c>yes</c>, <c>ok</c>, ...).
    /// </summary>
    public static NativeControl? FindButton(IReadOnlyList<NativeControl> controls, string button)
    {
        var clickable = controls
            .Where(c => c.IsVisible && c.Kind is NativeControlKind.Button or NativeControlKind.CheckBox or NativeControlKind.RadioButton)
            .ToList();

        var byRef = clickable.FirstOrDefault(c => string.Equals(c.Ref, button, StringComparison.OrdinalIgnoreCase));
        if (byRef != null) return byRef;

        var wanted = Normalize(button);
        var exact = clickable.FirstOrDefault(c => Normalize(c.Text) == wanted);
        if (exact != null) return exact;

        if (StandardIds.TryGetValue(wanted.Replace(" ", ""), out var id))
        {
            var byId = clickable.FirstOrDefault(c => c.ControlId == id && c.Kind == NativeControlKind.Button);
            if (byId != null) return byId;
        }

        return wanted.Length == 0 ? null : clickable.FirstOrDefault(c => Normalize(c.Text).StartsWith(wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// Presses a button asynchronously (PostMessage), so this call never waits on the app.
    /// Push buttons get WM_COMMAND/BN_CLICKED sent to their parent, which is how dialogs
    /// (including MessageBox) and WinForms (via message reflection) handle clicks. Check boxes
    /// and radio buttons get BM_CLICK so their state toggles.
    /// </summary>
    public static bool Press(NativeControl control)
    {
        if (control.Kind == NativeControlKind.Button && control.Parent != 0)
        {
            var wParam = (nint)((control.ControlId & 0xFFFF) | (NativeMethods.BN_CLICKED << 16));
            return NativeMethods.PostMessage(control.Parent, NativeMethods.WM_COMMAND, wParam, control.Hwnd);
        }
        return NativeMethods.PostMessage(control.Hwnd, NativeMethods.BM_CLICK, 0, 0);
    }

    /// <summary>Posts WM_CLOSE: the same as the title-bar X (Esc/Cancel for most dialogs).</summary>
    public static bool RequestClose(nint dialog) =>
        NativeMethods.PostMessage(dialog, NativeMethods.WM_CLOSE, 0, 0);

    public static bool SetText(NativeControl control, string text) =>
        NativeMethods.SetTextWithTimeout(control.Hwnd, text);

    public static string Describe(NativeControl c)
    {
        var parts = new List<string> { c.Ref, c.Kind.ToString().ToLowerInvariant() };
        if (c.Text.Length > 0) parts.Add($"\"{Escape(c.Text)}\"");
        if (c.IsDefault) parts.Add("[default]");
        if (c.IsChecked == true) parts.Add("[checked]");
        if (!c.IsEnabled) parts.Add("[disabled]");
        if (!c.IsVisible) parts.Add("[hidden]");
        if (c.ControlId > 0 && c.ControlId < 0xFFFF) parts.Add($"id={c.ControlId}");
        return string.Join(" ", parts);
    }

    private static NativeControlKind Classify(string cls, long style)
    {
        // WinForms class names look like "WindowsForms10.BUTTON.app.0.2bf8098_r6_ad1".
        if (Contains(cls, "BUTTON"))
        {
            return (style & 0x0F) switch
            {
                0x2 or 0x3 or 0x5 or 0x6 => NativeControlKind.CheckBox,
                0x4 or 0x9 => NativeControlKind.RadioButton,
                0x7 => NativeControlKind.GroupBox,
                _ => NativeControlKind.Button,
            };
        }
        if (Contains(cls, "EDIT") || Contains(cls, "RichEdit")) return NativeControlKind.Edit;
        if (Contains(cls, "COMBOBOX")) return NativeControlKind.ComboBox;
        if (Contains(cls, "STATIC")) return NativeControlKind.Text;
        return NativeControlKind.Other;
    }

    private static bool Contains(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string s) => s.Replace("&", "").Trim().ToLowerInvariant();

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
}
