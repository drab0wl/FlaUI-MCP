using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Snapshots;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Draws the refs of a window's interactive elements onto its screenshot, for apps whose
/// accessibility tree is too thin to act from text alone: look at the picture, act on the ref.
/// </summary>
public sealed class ScreenshotAnnotator
{
    private const int MaxElements = 150;
    private const int MaxLegend = 60;

    private static readonly ControlType[] Interactive =
    {
        ControlType.Button, ControlType.SplitButton, ControlType.Edit, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.ListItem, ControlType.MenuItem, ControlType.TabItem, ControlType.TreeItem,
        ControlType.Hyperlink, ControlType.Slider, ControlType.Spinner, ControlType.DataItem, ControlType.Document,
    };

    private readonly ElementRegistry _elements;

    public ScreenshotAnnotator(ElementRegistry elements)
    {
        _elements = elements;
    }

    /// <param name="png">The screenshot.</param>
    /// <param name="window">The window the elements belong to.</param>
    /// <param name="windowHandle">Its handle, so the refs drawn are usable with other tools.</param>
    /// <param name="capture">Screen rectangle the screenshot covers.</param>
    /// <returns>The annotated PNG and a legend (ref, role, name), or null if UI Automation didn't answer.</returns>
    public (byte[] Png, IReadOnlyList<string> Legend)? Annotate(byte[] png, AutomationElement window, string windowHandle, Rectangle capture)
    {
        // A pending click can hold the app's provider: don't hang the screenshot on it.
        if (!Responsiveness.Probe(() => _ = window.Properties.Name.ValueOrDefault, TimeSpan.FromMilliseconds(750))) return null;

        var automation = window.Automation;
        var p = automation.PropertyLibrary;
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (var id in new[] { p.Element.Name, p.Element.AutomationId, p.Element.ControlType, p.Element.BoundingRectangle, p.Element.IsOffscreen, p.Element.RuntimeId })
        {
            request.Add(id);
        }

        var candidates = new List<(string Ref, Rectangle Screen, string Line)>();
        using (request.Activate())
        {
            var cf = window.ConditionFactory;
            var condition = new FlaUI.Core.Conditions.OrCondition(Interactive.Select(t => (FlaUI.Core.Conditions.ConditionBase)cf.ByControlType(t)));
            foreach (var element in window.FindAllDescendants(condition))
            {
                var e = element.FrameworkAutomationElement;
                if (e.TryGetPropertyValue<bool>(p.Element.IsOffscreen, out var offscreen) && offscreen) continue;
                if (!e.TryGetPropertyValue<Rectangle>(p.Element.BoundingRectangle, out var rect) || !rect.IntersectsWith(capture)) continue;

                var node = SnapshotBuilder.ReadCached(element, automation);
                var refId = _elements.RegisterFound(windowHandle, element, node.RuntimeId);
                var name = SnapshotFormat.DisplayName(node.Name, node.AutomationId);
                candidates.Add((refId, rect, SnapshotFormat.Line(refId, name, SnapshotFormat.Role(node.ControlType), Array.Empty<string>())));
                if (candidates.Count >= MaxElements) break;
            }
        }

        var boxes = AnnotationLayout.Place(candidates.Select(c => (c.Ref, c.Screen)), capture, MaxElements);
        using var bitmap = new Bitmap(new MemoryStream(png));
        using (var g = Graphics.FromImage(bitmap))
        using (var pen = new Pen(Color.FromArgb(230, 255, 0, 170), 2))
        using (var font = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var labelBrush = new SolidBrush(Color.FromArgb(230, 255, 0, 170)))
        using (var textBrush = new SolidBrush(Color.White))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            foreach (var box in boxes)
            {
                g.DrawRectangle(pen, box.Box);
                var size = g.MeasureString(box.Ref, font);
                var label = new RectangleF(box.Label.X, box.Label.Y, size.Width + 2, AnnotationLayout.LabelHeight);
                g.FillRectangle(labelBrush, label);
                g.DrawString(box.Ref, font, textBrush, label.X + 1, label.Y + 1);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var drawn = boxes.Select(b => b.Ref).ToHashSet();
        var legend = candidates.Where(c => drawn.Contains(c.Ref)).Select(c => c.Line).Take(MaxLegend).ToList();
        if (drawn.Count > legend.Count) legend.Add($"... and {drawn.Count - legend.Count} more on the image");
        return (stream.ToArray(), legend);
    }
}
