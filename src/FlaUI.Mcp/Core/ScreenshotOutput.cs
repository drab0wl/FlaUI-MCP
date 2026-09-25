namespace PlaywrightWindows.Mcp.Core;

/// <summary>How windows_screenshot hands back the picture.</summary>
public enum ScreenshotOutput
{
    /// <summary>The PNG inline (base64). Some clients truncate large ones.</summary>
    Image,
    /// <summary>Save the PNG and return only its path and size.</summary>
    File,
    /// <summary>Save the full PNG, and return its path plus a small JPEG preview inline.</summary>
    Preview,
}

/// <summary>Pure helpers for screenshot output. FLAUI_MCP_SCREENSHOT_OUTPUT sets the default.</summary>
public static class ScreenshotOutputs
{
    public const int PreviewMaxSide = 800;

    public static ScreenshotOutput? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "image" or "inline" or "base64" => ScreenshotOutput.Image,
        "file" or "path" => ScreenshotOutput.File,
        "preview" or "thumbnail" => ScreenshotOutput.Preview,
        _ => null,
    };

    public static ScreenshotOutput Default { get; } =
        Parse(Environment.GetEnvironmentVariable("FLAUI_MCP_SCREENSHOT_OUTPUT")) ?? ScreenshotOutput.Image;

    /// <summary>Where a screenshot goes when no savePath is given: a timestamped file under the temp folder.</summary>
    public static string DefaultPath(string tempDirectory, DateTime now, string? label = null)
    {
        var safe = string.IsNullOrEmpty(label) ? "" : "-" + new string(label.Where(char.IsLetterOrDigit).Take(20).ToArray());
        return Path.Combine(tempDirectory, "flaui-mcp", $"screenshot-{now:yyyyMMdd-HHmmss-fff}{safe}.png");
    }

    /// <summary>Scale to fit in a max×max box, keeping the aspect ratio and never enlarging.</summary>
    public static (int Width, int Height) PreviewSize(int width, int height, int max = PreviewMaxSide)
    {
        if (width <= 0 || height <= 0) return (Math.Max(width, 1), Math.Max(height, 1));
        var scale = Math.Min(1.0, (double)max / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
