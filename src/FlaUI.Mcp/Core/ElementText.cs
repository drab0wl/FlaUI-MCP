using FlaUI.Core.AutomationElements;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// The text an element shows, as windows_get_text reports it.
/// </summary>
public static class ElementText
{
    public static string? Read(AutomationElement element)
    {
        string? text = null;

        // Try Value pattern first (for text inputs)
        if (element.Patterns.Value.IsSupported)
        {
            text = element.Patterns.Value.Pattern.Value.ValueOrDefault;
        }

        if (string.IsNullOrEmpty(text) && element.Patterns.Selection.IsSupported)
        {
            var selected = element.Patterns.Selection.Pattern.Selection.ValueOrDefault;
            if (selected != null && selected.Length > 0)
            {
                text = selected[0].Properties.Name.ValueOrDefault;
            }
        }

        if (string.IsNullOrEmpty(text) && element.Patterns.LegacyIAccessible.IsSupported)
        {
            text = element.Patterns.LegacyIAccessible.Pattern.Value.ValueOrDefault;
        }

        // Fall back to Name property
        if (string.IsNullOrEmpty(text))
        {
            text = element.Properties.Name.ValueOrDefault;
        }

        // Try Text pattern
        if (string.IsNullOrEmpty(text) && element.Patterns.Text.IsSupported)
        {
            text = element.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        }

        return text;
    }
}
