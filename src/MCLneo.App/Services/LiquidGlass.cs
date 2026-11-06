using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MCLneo.App.Services;

/// <summary>
/// Gives every ComboBox popup a small "liquid glass" settle animation:
/// the translucent panel scales up from its top edge with a soft overshoot.
/// </summary>
public static class LiquidGlass
{
    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnComboLoaded));
    }

    private static void OnComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.DropDownOpened -= OnDropDownOpened;
        combo.DropDownOpened += OnDropDownOpened;
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.Dispatcher.BeginInvoke(new Action(() => Animate(combo)), DispatcherPriority.Render);
    }

    private static void Animate(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: FrameworkElement panel })
        {
            return;
        }

        if (LogicalTreeHelper.FindLogicalNode(panel, "PopupGlassBorder") is Border glassBorder)
        {
            BackdropService.ApplyToBorder(panel, glassBorder);
        }

        if (LogicalTreeHelper.FindLogicalNode(panel, "PopupGlassBorder") is Border border &&
            border.ActualWidth > 1 && border.ActualHeight > 1)
        {
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight), 16, 16);
        }

        if (panel.ActualWidth > 1 && panel.ActualHeight > 1)
        {
            panel.Clip = new RectangleGeometry(
                new Rect(0, 0, panel.ActualWidth, panel.ActualHeight), 16, 16);
            RoundedMask.Apply(panel, 16);
        }

        panel.RenderTransformOrigin = new Point(0.5, 0);
        var scale = new ScaleTransform(0.965, 0.92);
        var slide = new TranslateTransform(0, 7);
        panel.RenderTransform = new TransformGroup { Children = { scale, slide } };
        panel.Opacity = 0;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.16 };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleX = new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
        var slideY = new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        panel.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        slide.BeginAnimation(TranslateTransform.YProperty, slideY);
    }
}
