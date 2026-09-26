using System.Drawing;

namespace PlaywrightWindows.Mcp.Core;

public sealed record AnnotationBox(string Ref, Rectangle Box, Point Label);

/// <summary>Where to draw ref boxes and labels on a screenshot. Pure.</summary>
public static class AnnotationLayout
{
    public const int LabelHeight = 14;

    /// <param name="elements">Screen rectangles of the elements.</param>
    /// <param name="capture">Screen rectangle the image covers.</param>
    public static IReadOnlyList<AnnotationBox> Place(IEnumerable<(string Ref, Rectangle Screen)> elements, Rectangle capture, int max = 200)
    {
        var result = new List<AnnotationBox>();
        foreach (var (refId, screen) in elements)
        {
            var clipped = Rectangle.Intersect(screen, capture);
            if (clipped.Width < 4 || clipped.Height < 4) continue;
            var box = clipped with { X = clipped.X - capture.X, Y = clipped.Y - capture.Y };
            // Label just above the box, or inside its top edge when there's no room above.
            var labelY = box.Y >= LabelHeight ? box.Y - LabelHeight : box.Y;
            result.Add(new AnnotationBox(refId, box, new Point(box.X, labelY)));
            if (result.Count >= max) break;
        }
        return result;
    }
}
