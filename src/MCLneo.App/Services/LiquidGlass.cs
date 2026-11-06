using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
    private static readonly HashSet<ComboBox> AttachedCombos = new();

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnComboLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnComboClick), true);
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnComboKeyDown), true);
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnComboWheel), true);
    }

    private static void OnComboWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen) return;

        // Do not let a closed ComboBox steal the wheel and change its value.
        // Forward the wheel to the surrounding scroll viewer so the page still scrolls.
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(combo);
        while (parent is not null and not ScrollViewer)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is ScrollViewer scrollViewer)
        {
            scrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            });
        }
    }

    private static void OnComboClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBox combo) EnsureAttached(combo);
    }

    private static void OnComboKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ComboBox combo && e.Key is Key.Enter or Key.Space or Key.Down)
        {
            EnsureAttached(combo);
        }
    }

    private static void EnsureAttached(ComboBox combo)
    {
        if (!AttachedCombos.Add(combo)) return;
        combo.DropDownOpened += OnDropDownOpened;
        MirrorResources(combo);
    }

    public static void AttachTree(DependencyObject root)
    {
        if (root is ComboBox combo)
        {
            EnsureAttached(combo);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            AttachTree(VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void OnComboLoaded(object sender, RoutedEventArgs e)
    {        if (sender is not ComboBox combo) return;
        combo.ApplyTemplate();
        combo.DropDownOpened -= OnDropDownOpened;
        combo.DropDownOpened += OnDropDownOpened;
        MirrorResources(combo);
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        MirrorResources(combo);
        combo.Dispatcher.BeginInvoke(new Action(() => Animate(combo)), DispatcherPriority.Render);
    }

    private static void MirrorResources(ComboBox combo)
    {
        try
        {            combo.ApplyTemplate();
            if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: FrameworkElement panel })
            {
                return;
            }

            var textBrush = Application.Current.Resources["TextBrush"] as Brush
                            ?? (ThemeService.IsDark ? Brushes.White : Brushes.Black);
            panel.SetValue(TextElement.ForegroundProperty, textBrush);            if (LogicalTreeHelper.FindLogicalNode(panel, "PopupGlassBorder") is Border border)
            {
                border.Background = Application.Current.Resources["PopupGlassBrush"] as Brush
                                    ?? new SolidColorBrush(ThemeService.IsDark
                                        ? Color.FromRgb(0x2A, 0x2A, 0x35)
                                        : Color.FromRgb(0xE2, 0xE5, 0xEC));
                border.SetValue(TextElement.ForegroundProperty, textBrush);            }

            if (Application.Current.Resources[typeof(ComboBoxItem)] is Style baseItemStyle)
            {
                var itemStyle = new Style(typeof(ComboBoxItem), baseItemStyle);
                itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, textBrush));
                combo.ItemContainerStyle = itemStyle;
            }
        }
        catch
        {
        }
    }
    private static void Animate(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: FrameworkElement panel })
        {
            return;
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
