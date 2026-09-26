using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using PlaywrightWindows.Mcp.Core;
using PlaywrightWindows.Mcp.Core.Win32;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Take a screenshot
/// </summary>
public class ScreenshotTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly ScreenshotAnnotator _annotator;

    public ScreenshotTool(SessionManager sessionManager, ElementRegistry elementRegistry)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _annotator = new ScreenshotAnnotator(elementRegistry);
    }

    public override string Name => "windows_screenshot";

    public override string Description =>
        "PNG of a window or element, for when the accessibility tree doesn't show what you need. " +
        "output=file saves it and returns just the path; output=preview also returns a small JPEG; " +
        "the default returns the full PNG inline. annotate=true draws the refs of buttons, fields, items etc. on it " +
        "(and lists them), so you can act on what you see.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle. If omitted, captures the foreground window."
            },
            @ref = new
            {
                type = "string",
                description = "Element ref to capture. If omitted, captures the whole window."
            },
            fullScreen = new
            {
                type = "boolean",
                description = "Capture the entire screen (default: false)"
            },
            background = new
            {
                type = "boolean",
                description = "Use native background window capture for a window handle, falling back to normal capture if unavailable (default: false)"
            },
            savePath = new
            {
                type = "string",
                description = "Absolute local .png file path to save the screenshot. UNC and device paths are rejected."
            },
            overwrite = new
            {
                type = "boolean",
                description = "Allow savePath to replace an existing file (default: false)"
            },
            annotate = new
            {
                type = "boolean",
                description = "Draw the refs of the window's interactive elements on the image and list them (default: false)"
            },
            output = new
            {
                type = "string",
                @enum = new[] { "image", "file", "preview" },
                description = $"image: the PNG inline. file: save it (to savePath, else a temp file) and return the path. " +
                              $"preview: save it and return the path plus a JPEG at most {ScreenshotOutputs.PreviewMaxSide}px. " +
                              "Default: image"
            }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var refId = GetStringArgument(arguments, "ref");
        var fullScreen = GetBoolArgument(arguments, "fullScreen", false);
        var background = GetBoolArgument(arguments, "background", false);
        var savePath = GetStringArgument(arguments, "savePath");
        var overwrite = GetBoolArgument(arguments, "overwrite", false);
        var annotate = GetBoolArgument(arguments, "annotate", false);
        var outputText = GetStringArgument(arguments, "output");
        var output = ScreenshotOutputs.Default;
        if (outputText != null)
        {
            if (ScreenshotOutputs.Parse(outputText) is not { } parsed)
            {
                return Task.FromResult(ErrorResult("output must be image, file or preview"));
            }
            output = parsed;
        }

        if (!TryNormalizeSavePath(savePath, overwrite, out var normalizedSavePath, out var pathError))
        {
            return Task.FromResult(ErrorResult(pathError));
        }

        try
        {
            CaptureImage? capture = null;
            byte[]? imageData = null;
            // For annotate: the screen area the image covers, and the window its elements belong to.
            System.Drawing.Rectangle? area = null;
            string? areaWindow = null;

            if (background && (fullScreen || !string.IsNullOrEmpty(refId) || string.IsNullOrEmpty(handle)))
            {
                return Task.FromResult(ErrorResult("background capture requires a window handle and cannot be combined with ref or fullScreen"));
            }

            if (fullScreen)
            {
                if (annotate) return Task.FromResult(ErrorResult("annotate needs a window: pass handle or ref, not fullScreen."));
                capture = Capture.Screen();
            }
            else if (!string.IsNullOrEmpty(refId))
            {
                var element = _elementRegistry.GetElement(refId);
                if (element == null)
                {
                    return Task.FromResult(ErrorResult($"Element not found: {refId}"));
                }
                capture = Capture.Element(element);
                area = element.BoundingRectangle;
                areaWindow = ElementRegistry.WindowHandleOf(refId);
            }
            else if (!string.IsNullOrEmpty(handle) && !background
                     && TryGetNativeBounds(_sessionManager.GetHwnd(handle), out var bounds))
            {
                // Screen-rectangle capture from the HWND: no UI Automation involved, so this
                // still works when a dialog has the app's UIA provider blocked.
                capture = Capture.Rectangle(bounds);
                area = bounds;
                areaWindow = handle;
            }
            else if (!string.IsNullOrEmpty(handle))
            {
                var window = _sessionManager.GetWindow(handle);
                if (window == null)
                {
                    return Task.FromResult(ErrorResult($"Window not found: {handle}"));
                }

                areaWindow = handle;
                if (background && NativeWindowCapture.TryCaptureWindow(window, out var backgroundImage, out _))
                {
                    imageData = backgroundImage;
                    if (TryGetNativeBounds(_sessionManager.GetHwnd(handle), out var backgroundBounds)) area = backgroundBounds;
                }
                else
                {
                    capture = Capture.Element(window);
                    area = window.BoundingRectangle;
                }
            }
            else
            {
                // Capture foreground window
                var focusedElement = _sessionManager.Automation.FocusedElement();
                if (focusedElement == null)
                {
                    return Task.FromResult(ErrorResult("No focused window found"));
                }

                // Walk up to find the window
                var current = focusedElement;
                while (current != null && current.Properties.ControlType.ValueOrDefault != FlaUI.Core.Definitions.ControlType.Window)
                {
                    current = current.Parent;
                }

                if (current == null)
                {
                    return Task.FromResult(ErrorResult("Could not find window for focused element"));
                }

                capture = Capture.Element(current);
                area = current.BoundingRectangle;
                if (annotate) areaWindow = _sessionManager.RegisterWindow(current.AsWindow());
            }

            if (capture != null)
            {
                using (capture)
                {
                    using var stream = new MemoryStream();
                    capture.Bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    imageData = stream.ToArray();
                }
            }

            string? note = null;
            if (annotate && area is { } covered && areaWindow != null && _sessionManager.GetWindow(areaWindow) is { } annotated)
            {
                var drawn = _annotator.Annotate(imageData!, annotated, areaWindow, covered);
                if (drawn is { } d)
                {
                    imageData = d.Png;
                    note = d.Legend.Count == 0
                        ? "No interactive elements found to label."
                        : "Refs on the image:\n" + string.Join("\n", d.Legend);
                }
                else
                {
                    note = "Refs not drawn: the app's UI Automation isn't answering (probably a pending click behind a dialog).";
                }
            }

            return Task.FromResult(BuildScreenshotResult(imageData!, normalizedSavePath, overwrite, output, refId ?? handle, note));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture screenshot: {ex.Message}"));
        }
    }

    internal static bool TryNormalizeSavePath(string? savePath, bool overwrite, out string? normalizedPath, out string error)
    {
        normalizedPath = null;
        error = "";

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return true;
        }

        if (!Path.IsPathFullyQualified(savePath))
        {
            error = $"savePath must be an absolute local path: {savePath}";
            return false;
        }

        if (savePath.StartsWith(@"\\") || savePath.StartsWith(@"\\?\") || savePath.StartsWith(@"\\.\"))
        {
            error = "savePath must be a local drive path; UNC and device paths are not allowed";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(savePath);
        }
        catch (Exception ex)
        {
            error = $"savePath is invalid: {ex.Message}";
            return false;
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "savePath must end with .png";
            return false;
        }

        if (File.Exists(fullPath) && !overwrite)
        {
            error = $"savePath already exists; pass overwrite=true to replace it: {fullPath}";
            return false;
        }

        normalizedPath = fullPath;
        return true;
    }

    private static bool TryGetNativeBounds(nint hwnd, out System.Drawing.Rectangle bounds)
    {
        bounds = default;
        if (hwnd == 0 || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)
            || !NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return false;
        }
        bounds = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static McpToolResult BuildScreenshotResult(byte[] imageData, string? savePath, bool overwrite, ScreenshotOutput output, string? label, string? note = null)
    {
        var result = BuildScreenshotResultCore(imageData, savePath, overwrite, output, label);
        if (note == null || result.IsError == true) return result;
        var content = result.Content.ToList();
        var text = content.FindIndex(c => c.Type == "text");
        if (text >= 0) content[text] = content[text] with { Text = $"{content[text].Text}\n{note}" };
        else content.Insert(0, new McpContent { Type = "text", Text = note });
        return result with { Content = content };
    }

    private static McpToolResult BuildScreenshotResultCore(byte[] imageData, string? savePath, bool overwrite, ScreenshotOutput output, string? label)
    {
        if (output == ScreenshotOutput.Image && string.IsNullOrEmpty(savePath))
        {
            return ImageResult(imageData, "image/png");
        }
        savePath ??= ScreenshotOutputs.DefaultPath(Path.GetTempPath(), DateTime.Now, label);

        try
        {
            var directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Combine(directory ?? Directory.GetCurrentDirectory(), $"{Path.GetFileName(savePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, imageData);
                File.Move(tempPath, savePath, overwrite);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed to save screenshot to {savePath}: {ex.Message}");
        }

        if (output == ScreenshotOutput.Image)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Screenshot saved to {savePath}" },
                    new() { Type = "image", Data = Convert.ToBase64String(imageData), MimeType = "image/png" }
                }
            };
        }

        using var bitmap = new System.Drawing.Bitmap(new MemoryStream(imageData));
        var text = $"Screenshot saved to {savePath} ({bitmap.Width}x{bitmap.Height}, {imageData.Length / 1024} KB)";
        var content = new List<McpContent> { new() { Type = "text", Text = text } };
        if (output == ScreenshotOutput.Preview)
        {
            content.Add(new McpContent { Type = "image", Data = Convert.ToBase64String(Preview(bitmap)), MimeType = "image/jpeg" });
        }
        return new McpToolResult { Content = content };
    }

    /// <summary>A downscaled JPEG, small enough to pass inline.</summary>
    private static byte[] Preview(System.Drawing.Bitmap source)
    {
        var (width, height) = ScreenshotOutputs.PreviewSize(source.Width, source.Height);
        using var scaled = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, width, height);
        }

        var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
        using var stream = new MemoryStream();
        scaled.Save(stream, jpeg, parameters);
        return stream.ToArray();
    }
}
