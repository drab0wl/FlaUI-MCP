using System.Globalization;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>Pure parsing helpers for state actions.</summary>
public static class ValueParsing
{
    /// <summary>Invariant culture first ("0.5"), then the current culture ("0,5").</summary>
    public static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
