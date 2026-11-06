using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MCLneo.App.Services;

/// <summary>
/// Crops a half-resolution snapshot of the main window to the popup's bounds.
/// The cropped bitmap is blurred by the target Image's BlurEffect, providing
/// a real frosted-glass backdrop instead of a flat alpha fill.
/// </summary>
public static class BackdropService
{
    public static bool Apply(FrameworkElement panel, Image target)
    {
        try
        {
            var window = Application.Current?.MainWindow;
            if (window?.Content is not FrameworkElement root ||
                root.ActualWidth < 2 || root.ActualHeight < 2 ||
                panel.ActualWidth < 2 || panel.ActualHeight < 2)
            {
                return false;
            }

            const double scale = 0.5;
            var dpi = VisualTreeHelper.GetDpi(root);
            var pixelWidth = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX * scale);
            var pixelHeight = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY * scale);
            if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > 4096 || pixelHeight > 4096)
            {
                return false;
            }

            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                96 * dpi.DpiScaleX * scale, 96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();

            const double padding = 44;
            var panelOrigin = panel.PointToScreen(new Point(0, 0));
            var rootOrigin = root.PointToScreen(new Point(0, 0));
            var padX = padding * dpi.DpiScaleX * scale;
            var padY = padding * dpi.DpiScaleY * scale;
            var x = (int)Math.Round((panelOrigin.X - rootOrigin.X) * scale - padX);
            var y = (int)Math.Round((panelOrigin.Y - rootOrigin.Y) * scale - padY);
            var width = (int)Math.Round(panel.ActualWidth * dpi.DpiScaleX * scale + padX * 2);
            var height = (int)Math.Round(panel.ActualHeight * dpi.DpiScaleY * scale + padY * 2);

            x = Math.Clamp(x, 0, Math.Max(0, pixelWidth - 1));
            y = Math.Clamp(y, 0, Math.Max(0, pixelHeight - 1));
            width = Math.Clamp(width, 1, pixelWidth - x);
            height = Math.Clamp(height, 1, pixelHeight - y);

            var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
            cropped.Freeze();
            target.Source = cropped;
            // Overscan the image so BlurEffect samples opaque pixels past the
            // panel edge instead of fading to transparent and creating a rim.
            target.Width = panel.ActualWidth + padding * 2;
            target.Height = panel.ActualHeight + padding * 2;
            target.Margin = new Thickness(-padding);
            target.HorizontalAlignment = HorizontalAlignment.Left;
            target.VerticalAlignment = VerticalAlignment.Top;
            target.Stretch = Stretch.Fill;
            target.Visibility = Visibility.Visible;
            return true;
        }
        catch
        {
            target.Source = null;
            return false;
        }
    }

    /// <summary>
    /// Uses a blurred snapshot as the Border's brush.  Brush content is clipped
    /// by Border.CornerRadius, unlike Image children, so all four corners stay
    /// perfectly rounded without masks or extra layers.
    /// </summary>
    public static bool ApplyToBorder(FrameworkElement panel, Border target)
    {
        try
        {
            var window = Application.Current?.MainWindow;
            if (window?.Content is not FrameworkElement root ||
                root.ActualWidth < 2 || root.ActualHeight < 2 ||
                panel.ActualWidth < 2 || panel.ActualHeight < 2)
            {
                return false;
            }

            const double scale = 0.5;
            const double padding = 44;
            var dpi = VisualTreeHelper.GetDpi(root);
            var pixelWidth = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX * scale);
            var pixelHeight = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY * scale);
            if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > 4096 || pixelHeight > 4096)
            {
                return false;
            }

            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                96 * dpi.DpiScaleX * scale, 96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();

            var panelOrigin = panel.PointToScreen(new Point(0, 0));
            var rootOrigin = root.PointToScreen(new Point(0, 0));
            var padX = padding * dpi.DpiScaleX * scale;
            var padY = padding * dpi.DpiScaleY * scale;
            var x = (int)Math.Round((panelOrigin.X - rootOrigin.X) * scale - padX);
            var y = (int)Math.Round((panelOrigin.Y - rootOrigin.Y) * scale - padY);
            var width = (int)Math.Round(panel.ActualWidth * dpi.DpiScaleX * scale + padX * 2);
            var height = (int)Math.Round(panel.ActualHeight * dpi.DpiScaleY * scale + padY * 2);

            x = Math.Clamp(x, 0, Math.Max(0, pixelWidth - 1));
            y = Math.Clamp(y, 0, Math.Max(0, pixelHeight - 1));
            width = Math.Clamp(width, 1, pixelWidth - x);
            height = Math.Clamp(height, 1, pixelHeight - y);

            var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
            cropped.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(cropped, new Rect(0, 0, width, height));
            }
            visual.Effect = new BlurEffect { Radius = 22, KernelType = KernelType.Gaussian };

            var blurred = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            blurred.Render(visual);

            var originX = (int)Math.Round((panelOrigin.X - rootOrigin.X) * scale - x);
            var originY = (int)Math.Round((panelOrigin.Y - rootOrigin.Y) * scale - y);
            var panelPixelWidth = Math.Max(1, (int)Math.Round(panel.ActualWidth * dpi.DpiScaleX * scale));
            var panelPixelHeight = Math.Max(1, (int)Math.Round(panel.ActualHeight * dpi.DpiScaleY * scale));
            originX = Math.Clamp(originX, 0, Math.Max(0, width - 1));
            originY = Math.Clamp(originY, 0, Math.Max(0, height - 1));
            panelPixelWidth = Math.Clamp(panelPixelWidth, 1, width - originX);
            panelPixelHeight = Math.Clamp(panelPixelHeight, 1, height - originY);

            var center = new CroppedBitmap(blurred,
                new Int32Rect(originX, originY, panelPixelWidth, panelPixelHeight));
            center.Freeze();
            target.Background = new ImageBrush(center) { Stretch = Stretch.Fill };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
