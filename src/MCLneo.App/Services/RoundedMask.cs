using System.Windows;
using System.Windows.Media;

namespace MCLneo.App.Services;

/// <summary>
/// WPF Border does not clip children to CornerRadius.  An opacity mask made
/// from the same rounded-rectangle geometry is a reliable way to round the
/// whole popup, including transparent bitmap corners.
/// </summary>
public static class RoundedMask
{
    public static void Apply(FrameworkElement element, double radius)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return;

        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var geometry = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        var drawing = new GeometryDrawing(Brushes.Black, null, geometry);
        var brush = new DrawingBrush(drawing)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, width, height),
            Viewport = new Rect(0, 0, width, height)
        };
        brush.Freeze();
        element.OpacityMask = brush;
    }
}
