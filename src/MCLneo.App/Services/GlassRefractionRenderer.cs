using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MCLneo.App.Services;

/// <summary>
/// Renders a small liquid-glass backdrop entirely in software.  The window
/// behind the popup is captured, blurred, and then sampled through a rounded
/// squircle lens: only the outer band is displaced, the center stays clean.
/// This mirrors the Web/Skia approach used by LiquidGlassUI without requiring
/// an HLSL build step.
/// </summary>
public static class GlassRefractionRenderer
{
    private const double Corner = 16;
    private const double Band = 13;
    private const double Lens = 7;
    private const int Overscan = 40;

    public static bool Apply(FrameworkElement panel, Image target, bool dark)
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

            var dpi = VisualTreeHelper.GetDpi(root);
            var sx = dpi.DpiScaleX;
            var sy = dpi.DpiScaleY;
            var fullW = (int)Math.Ceiling(root.ActualWidth * sx);
            var fullH = (int)Math.Ceiling(root.ActualHeight * sy);
            if (fullW <= 0 || fullH <= 0 || fullW > 8192 || fullH > 8192)
            {
                return false;
            }

            var full = new RenderTargetBitmap(fullW, fullH, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            full.Render(root);
            full.Freeze();

            var panelOrigin = panel.PointToScreen(new Point(0, 0));
            var rootOrigin = root.PointToScreen(new Point(0, 0));
            var pad = (int)Math.Ceiling(Overscan * Math.Max(sx, sy));
            var rawX = (int)Math.Round(panelOrigin.X - rootOrigin.X) - pad;
            var rawY = (int)Math.Round(panelOrigin.Y - rootOrigin.Y) - pad;
            var rawW = (int)Math.Round(panel.ActualWidth * sx) + pad * 2;
            var rawH = (int)Math.Round(panel.ActualHeight * sy) + pad * 2;

            var cropX = Math.Clamp(rawX, 0, Math.Max(0, fullW - 1));
            var cropY = Math.Clamp(rawY, 0, Math.Max(0, fullH - 1));
            var cropW = Math.Clamp(rawW, 1, fullW - cropX);
            var cropH = Math.Clamp(rawH, 1, fullH - cropY);
            var originX = Math.Max(0, rawX - cropX);
            var originY = Math.Max(0, rawY - cropY);

            var crop = new CroppedBitmap(full, new Int32Rect(cropX, cropY, cropW, cropH));
            crop.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(crop, new Rect(0, 0, cropW, cropH));
            }
            visual.Effect = new BlurEffect
            {
                Radius = 17 * Math.Max(sx, sy),
                KernelType = KernelType.Gaussian
            };

            var blurred = new RenderTargetBitmap(cropW, cropH, 96, 96, PixelFormats.Pbgra32);
            blurred.Render(visual);
            var stride = cropW * 4;
            var source = new byte[stride * cropH];
            blurred.CopyPixels(source, stride, 0);

            var outW = (int)Math.Round(panel.ActualWidth * sx);
            var outH = (int)Math.Round(panel.ActualHeight * sy);
            if (outW < 2 || outH < 2)
            {
                return false;
            }

            var width = panel.ActualWidth;
            var height = panel.ActualHeight;
            var output = new byte[outW * 4 * outH];
            var tintR = dark ? 42d : 232d;
            var tintG = dark ? 42d : 235d;
            var tintB = dark ? 53d : 242d;
            var tintA = dark ? 0.47 : 0.38;

            for (var y = 0; y < outH; y++)
            {
                var localY = (y + 0.5) / sy;
                for (var x = 0; x < outW; x++)
                {
                    var localX = (x + 0.5) / sx;
                    var sd = RoundRectSdf(localX, localY, width, height, out var nx, out var ny);
                    var o = (y * outW + x) * 4;
                    if (sd > 1.2)
                    {
                        output[o + 3] = 0;
                        continue;
                    }

                    var rim = Math.Clamp(1 + sd / Band, 0, 1);
                    var lens = Lens * rim * rim;
                    var sampleX = originX + x + nx * lens * sx;
                    var sampleY = originY + y + ny * lens * sy;

                    Sample(source, cropW, cropH, sampleX, sampleY, out var b, out var g, out var r);
                    if (rim > 0.02)
                    {
                        // Slight chromatic split at the refracting edge.
                        Sample(source, cropW, cropH,
                            originX + x + nx * lens * 1.045 * sx,
                            originY + y + ny * lens * 1.045 * sy,
                            out _, out _, out var rr);
                        Sample(source, cropW, cropH,
                            originX + x + nx * lens * 0.955 * sx,
                            originY + y + ny * lens * 0.955 * sy,
                            out var bb, out _, out _);
                        r = (byte)((r + rr) / 2);
                        b = (byte)((b + bb) / 2);
                    }

                    var valueR = r * (1 - tintA) + tintR * tintA;
                    var valueG = g * (1 - tintA) + tintG * tintA;
                    var valueB = b * (1 - tintA) + tintB * tintA;

                    var light = Math.Max(0, -nx * 0.55 - ny * 0.83);
                    var specular = rim * (0.025 + 0.14 * light);
                    var shade = rim * Math.Max(0, ny) * 0.12;
                    valueR = Math.Clamp(valueR + 255 * specular - 255 * shade, 0, 255);
                    valueG = Math.Clamp(valueG + 255 * specular - 255 * shade, 0, 255);
                    valueB = Math.Clamp(valueB + 255 * specular - 255 * shade, 0, 255);

                    var coverage = Math.Clamp(1 - sd, 0, 1);
                    output[o] = (byte)valueB;
                    output[o + 1] = (byte)valueG;
                    output[o + 2] = (byte)valueR;
                    output[o + 3] = (byte)(coverage * 255);
                }
            }

            var bitmap = new WriteableBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, outW, outH), output, outW * 4, 0);
            bitmap.Freeze();

            target.Source = bitmap;
            target.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 9, 9);
            target.Width = width;
            target.Height = height;
            target.Margin = new Thickness(0);
            target.HorizontalAlignment = HorizontalAlignment.Stretch;
            target.VerticalAlignment = VerticalAlignment.Stretch;
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

    private static double RoundRectSdf(double x, double y, double width, double height, out double nx, out double ny)
    {
        var px = x - width / 2;
        var py = y - height / 2;
        var qx = Math.Abs(px) - (width / 2 - Corner);
        var qy = Math.Abs(py) - (height / 2 - Corner);
        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) +
                                Math.Max(qy, 0) * Math.Max(qy, 0));
        var sdf = outside + Math.Min(Math.Max(qx, qy), 0) - Corner;

        if (qx > 0 && qy > 0)
        {
            var len = Math.Sqrt(qx * qx + qy * qy);
            nx = Math.Sign(px) * qx / len;
            ny = Math.Sign(py) * qy / len;
        }
        else if (qx > qy)
        {
            nx = Math.Sign(px);
            ny = 0;
        }
        else
        {
            nx = 0;
            ny = Math.Sign(py);
        }

        return sdf;
    }

    private static void Sample(byte[] source, int width, int height,
        double x, double y, out byte b, out byte g, out byte r)
    {
        x = Math.Clamp(x, 0, width - 1.001);
        y = Math.Clamp(y, 0, height - 1.001);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var fx = x - x0;
        var fy = y - y0;

        var i00 = (y0 * width + x0) * 4;
        var i10 = (y0 * width + x1) * 4;
        var i01 = (y1 * width + x0) * 4;
        var i11 = (y1 * width + x1) * 4;

        b = Blend(source[i00], source[i10], source[i01], source[i11], fx, fy);
        g = Blend(source[i00 + 1], source[i10 + 1], source[i01 + 1], source[i11 + 1], fx, fy);
        r = Blend(source[i00 + 2], source[i10 + 2], source[i01 + 2], source[i11 + 2], fx, fy);
    }

    private static byte Blend(byte c00, byte c10, byte c01, byte c11, double fx, double fy) =>
        (byte)((c00 * (1 - fx) + c10 * fx) * (1 - fy) +
               (c01 * (1 - fx) + c11 * fx) * fy);
}
