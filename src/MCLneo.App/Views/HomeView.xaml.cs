using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MCLneo.App.Services;
using MCLneo.Core.Models;

namespace MCLneo.App.Views;

public partial class HomeView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly Action<string> _navigate;

    public HomeView(AppServices services, AppState state, Action<string> navigate)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        _navigate = navigate;
        DataContext = state;
        Loaded += (_, _) => StartDialPulse();
    }

    private void StartDialPulse()
    {
        if (DialPulseRing is null || DialPulseScale is null) return;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var fade = new DoubleAnimation(0.30, 0.72, TimeSpan.FromSeconds(2.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var breathe = new DoubleAnimation(1, 1.025, TimeSpan.FromSeconds(2.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        DialPulseRing.BeginAnimation(OpacityProperty, fade);
        DialPulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, breathe);
        DialPulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, breathe);
    }

    private void LaunchDial_MouseEnter(object sender, MouseEventArgs e)
    {
        if (DialScale is null) return;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        DialScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.07, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        DialScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.07, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void LaunchDial_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DialScale is null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DialScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.07, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        DialScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.07, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        await _state.LaunchAsync(_state.SelectedInstance);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            _navigate("instances");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{instance.Directory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开目录：" + ex.Message, true);
        }
    }

    private void InstanceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameInstance instance })
        {
            _state.SelectedInstance = instance;
            _navigate("instances");
        }
    }

    private void GoInstances_Click(object sender, RoutedEventArgs e) => _navigate("instances");
    private void GoDownload_Click(object sender, RoutedEventArgs e) => _navigate("download");
    private void GoMods_Click(object sender, RoutedEventArgs e) => _navigate("mods");
    private void GoSaves_Click(object sender, RoutedEventArgs e) => _navigate("saves");
    private void GoRoom_Click(object sender, RoutedEventArgs e) => _navigate("room");
}
