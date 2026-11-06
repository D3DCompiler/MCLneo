using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MCLneo.App.Services;
using MCLneo.App.Views;

namespace MCLneo.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _toastTimer;
    private bool _navigating;
    private bool _startupComplete;
    private bool _allowClose;
    private bool _closeAnimationRunning;
    private bool _stateAnimationRunning;
    private WindowState _lastWindowState = WindowState.Normal;
    private int _transitionToken;
    private Image? _themeSnapshot;
    private int _logoTaps;
    private DateTime _lastLogoTap = DateTime.MinValue;
    private string _currentPage = "home";
    private readonly string _initialPage;
    private ScaleTransform? _rootScale;
    private TranslateTransform? _rootTranslate;
    private TranslateTransform? _sidebarTranslate;
    private TranslateTransform? _contentTranslate;

    public MainWindow(AppServices services, AppState state, string? initialPage = null)
    {
        _initialPage = string.IsNullOrWhiteSpace(initialPage) ? "home" : initialPage;
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        var area = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(SettingsWidth(), MinWidth), area.Width);
        Height = Math.Min(Math.Max(SettingsHeight(), MinHeight), area.Height);
        _state.PropertyChanged += State_PropertyChanged;
        _state.ToastRequested += ShowToast;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SizeChanged += (_, _) => ApplyRoundedRegion();
        AccountMenuPanel.SizeChanged += (_, _) =>
        {
            ApplyPanelClip(AccountMenuPanel, 16);
            RoundedMask.Apply(AccountMenuPanel, 16);
        };
        ThemeService.ThemeChanging += ThemeService_ThemeChanging;
        ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        PrepareAnimationTransforms();
        Loaded += (_, _) =>
        {
            Navigate(_initialPage, force: true, animate: false);
            UpdateGameRunningChip();
            UpdateThemeToggle();
            PlayStartupAnimation();
        };
    }

    private void PrepareAnimationTransforms()
    {
        _rootScale = new ScaleTransform(0.985, 0.985);
        _rootTranslate = new TranslateTransform(0, 10);
        WindowRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        WindowRoot.RenderTransform = new TransformGroup
        {
            Children = { _rootScale, _rootTranslate }
        };
        WindowRoot.Opacity = 1;

        _sidebarTranslate = new TranslateTransform(-18, 0);
        SidebarRoot.RenderTransform = _sidebarTranslate;
        SidebarRoot.Opacity = 1;

        _contentTranslate = new TranslateTransform(0, 12);
        PageTransitionRoot.RenderTransform = _contentTranslate;
        PageTransitionRoot.Opacity = 1;

        // Start hidden; the Loaded handler plays the reveal.
        WindowRoot.Opacity = 0;
        SidebarRoot.Opacity = 0;
        PageTransitionRoot.Opacity = 0;
    }

    private double SettingsWidth() => Math.Max(MinWidth, _state.Settings.WindowWidth);
    private double SettingsHeight() => Math.Max(MinHeight, _state.Settings.WindowHeight);

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsGameRunning) or nameof(AppState.HasActiveGame))
        {
            UpdateGameRunningChip();
        }
    }

    private void UpdateGameRunningChip()
    {
        GameRunningChip.Visibility = _state.IsGameRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Navigate(string key, bool force = false, bool animate = true)
    {
        if (!force && string.Equals(_currentPage, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _navigating = true;
        try
        {
            if (!_pages.TryGetValue(key, out var page))
            {
                page = CreatePage(key);
                _pages[key] = page;
            }

            var outgoing = PageHost.Content as UserControl;
            var direction = PageIndex(key) >= PageIndex(_currentPage) ? 1 : -1;
            _currentPage = key;

            if (animate && _startupComplete && outgoing is not null &&
                !ReferenceEquals(outgoing, page))
            {
                // Keep the outgoing page in a second layer so both pages can animate.
                PageHost.Content = null;
                PageHostOld.Content = outgoing;
                PageHost.Content = page;
                PageHostOld.Visibility = Visibility.Visible;
                AnimatePageTransition(outgoing, page, direction);
            }
            else
            {
                PageHostOld.Content = null;
                PageHostOld.Visibility = Visibility.Collapsed;
                PageHost.Content = page;
                PageHost.Opacity = 1;
                PageHost.RenderTransform = Transform.Identity;
            }

            ApplyPageHeader(key);
            SetNavSelection(key);
        }
        finally
        {
            _navigating = false;
        }
    }

    private static int PageIndex(string key) => key switch
    {
        "home" => 0,
        "instances" => 1,
        "download" => 2,
        "modpacks" => 3,
        "mods" => 4,
        "saves" => 5,
        "room" => 6,
        "servers" => 7,
        "settings" => 8,
        _ => 0
    };

    private void AnimatePageTransition(UserControl outgoing, UserControl incoming, int direction)
    {
        var token = ++_transitionToken;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var oldTransform = new TranslateTransform(0, 0);
        var newTransform = new TranslateTransform(direction * 18, 0);
        PageHostOld.RenderTransform = oldTransform;
        PageHost.RenderTransform = newTransform;

        var oldOpacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = easeIn,
            FillBehavior = FillBehavior.HoldEnd
        };
        var newOpacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease
        };
        var oldSlide = new DoubleAnimation(0, direction * -16, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = easeIn,
            FillBehavior = FillBehavior.HoldEnd
        };
        var newSlide = new DoubleAnimation(direction * 18, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = ease
        };

        oldOpacity.Completed += (_, _) =>
        {
            if (token != _transitionToken) return;
            PageHostOld.Content = null;
            PageHostOld.Visibility = Visibility.Collapsed;
            PageHostOld.Opacity = 1;
            PageHostOld.RenderTransform = Transform.Identity;
        };

        PageHostOld.BeginAnimation(OpacityProperty, oldOpacity);
        oldTransform.BeginAnimation(TranslateTransform.XProperty, oldSlide);
        PageHost.BeginAnimation(OpacityProperty, newOpacity);
        newTransform.BeginAnimation(TranslateTransform.XProperty, newSlide);
    }

    private void PlayStartupAnimation()
    {
        if (_rootScale is null || _rootTranslate is null || _sidebarTranslate is null ||
            _contentTranslate is null)
        {
            WindowRoot.Opacity = 1;
            _startupComplete = true;
            return;
        }

        WindowRoot.Opacity = 1;
        _rootScale.ScaleX = 1;
        _rootScale.ScaleY = 1;
        _rootTranslate.Y = 0;
        SidebarRoot.Opacity = 1;
        _sidebarTranslate.X = 0;
        PageTransitionRoot.Opacity = 1;
        _contentTranslate.Y = 0;

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        WindowRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(430)) { EasingFunction = ease });
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(430)) { EasingFunction = ease });
        _rootTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(430)) { EasingFunction = ease });
        SidebarRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = ease
            });
        _sidebarTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(420))
            {
                BeginTime = TimeSpan.FromMilliseconds(60),
                EasingFunction = ease
            });
        PageTransitionRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = ease
            });
        _contentTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(460))
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = ease
            });

        var finish = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        finish.Tick += (_, _) =>
        {
            finish.Stop();
            _startupComplete = true;
        };
        finish.Start();
    }

    private UserControl CreatePage(string key) => key switch
    {
        "instances" => new InstancesView(_services, _state),
        "download" => new DownloadView(_services, _state),
        "modpacks" => new ModpacksView(_services, _state, key => Navigate(key)),
        "mods" => new ModsView(_services, _state),
        "saves" => new SavesView(_services, _state),
        "room" => new RoomView(_services, _state),
        "servers" => new ServersView(_services, _state),
        "settings" => new SettingsView(_services, _state),
        _ => new HomeView(_services, _state, key => Navigate(key))
    };

    private void ApplyPageHeader(string key)
    {
        (PageTitleText.Text, PageSubtitleText.Text) = key switch
        {
            "instances" => ("实例管理", "每个世界，都有一套自己的配置"),
            "download" => ("下载中心", "安装原版与加载器，所有文件走可信源"),
            "modpacks" => ("整合包", "一键搭建整合包实例"),
            "mods" => ("模组与资源", "从 Modrinth 发现新的玩法"),
            "saves" => ("存档管理", "备份、迁移，别让冒险消失"),
            "room" => ("联机房间", "把单人世界分享给朋友"),
            "servers" => ("服务器", "保存地址，一键启动连接"),
            "settings" => ("设置", "调整 Java、内存与下载源"),
            _ => ("主页", "欢迎回来")
        };
    }

    private void SetNavSelection(string key)
    {
        var target = key switch
        {
            "instances" => NavInstances,
            "download" => NavDownload,
            "modpacks" => NavModpacks,
            "mods" => NavMods,
            "saves" => NavSaves,
            "room" => NavRoom,
            "servers" => NavServers,
            "settings" => NavSettings,
            _ => NavHome
        };
        target.IsChecked = true;
    }



    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navigating || sender is not RadioButton { IsChecked: true, Tag: string key })
        {
            return;
        }

        Navigate(key);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeAnimated();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_stateAnimationRunning) return;
        if (!_startupComplete)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        _stateAnimationRunning = true;
        try
        {
            await AnimateWindowShell(1, 0.94, 1, 0, 140,
                new CubicEase { EasingMode = EasingMode.EaseIn });
            WindowState = WindowState.Minimized;
        }
        finally
        {
            _stateAnimationRunning = false;
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximizeAnimated();

    private async void ToggleMaximizeAnimated()
    {
        if (_stateAnimationRunning) return;
        if (!_startupComplete)
        {
            ToggleMaximize();
            return;
        }

        _stateAnimationRunning = true;
        try
        {
            await AnimateWindowShell(1, 0.985, 1, 1, 90,
                new CubicEase { EasingMode = EasingMode.EaseIn });
            ToggleMaximize();
            await Task.Delay(35);
            await AnimateWindowShell(0.985, 1, 1, 1, 220,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 });
        }
        finally
        {
            _stateAnimationRunning = false;
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private Task AnimateWindowShell(double fromScale, double toScale, double fromOpacity, double toOpacity,
        int milliseconds, IEasingFunction easing)
    {
        if (_rootScale is null)
        {
            return Task.CompletedTask;
        }

        _rootScale.ScaleX = toScale;
        _rootScale.ScaleY = toScale;
        WindowRoot.Opacity = toOpacity;

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var scaleX = new DoubleAnimation(fromScale, toScale, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var scaleY = new DoubleAnimation(fromScale, toScale, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var opacity = new DoubleAnimation(fromOpacity, toOpacity, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var completion = new TaskCompletionSource();
        opacity.Completed += (_, _) => completion.TrySetResult();

        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        WindowRoot.BeginAnimation(OpacityProperty, opacity);
        return completion.Task;
    }

    private void Logo_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLogoTap).TotalSeconds > 1.3)
        {
            _logoTaps = 0;
        }

        _lastLogoTap = now;
        _logoTaps++;
        if (_logoTaps < 5)
        {
            return;
        }

        _logoTaps = 0;
        PlayBlockRain();
        _state.Notify("隐藏区块已解锁 · 你挖到了第 64 层", false);
    }

    private void PlayBlockRain()
    {
        var width = Math.Max(300, EasterEggLayer.ActualWidth);
        var height = Math.Max(300, EasterEggLayer.ActualHeight);
        var random = new Random();
        var brushes = new[] { "AccentBrush", "AccentHoverBrush", "BlueBrush", "WarningBrush", "SuccessBrush" };
        for (var i = 0; i < 34; i++)
        {
            var size = random.Next(7, 18);
            var block = new Rectangle
            {
                Width = size,
                Height = size,
                RadiusX = 2,
                RadiusY = 2,
                Fill = (Brush)FindResource(brushes[random.Next(brushes.Length)]),
                Opacity = 0.9,
                RenderTransform = new RotateTransform(random.Next(0, 360), size / 2.0, size / 2.0)
            };
            Canvas.SetLeft(block, random.Next(20, Math.Max(21, (int)width - 20)));
            Canvas.SetTop(block, -random.Next(20, 160));
            EasterEggLayer.Children.Add(block);

            var duration = TimeSpan.FromMilliseconds(random.Next(900, 1600));
            var delay = TimeSpan.FromMilliseconds(random.Next(0, 360));
            var fall = new DoubleAnimation(-40, height + 80, duration)
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fall.Completed += (_, _) => EasterEggLayer.Children.Remove(block);
            block.BeginAnimation(Canvas.TopProperty, fall);
            block.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, duration) { BeginTime = delay });
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            ThemeToggle_Click(sender, new RoutedEventArgs());
        }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.ToggleMode();
        _state.Settings.ThemeMode = ThemeService.CurrentMode;
        _state.Settings.AccentTheme = ThemeService.CurrentTheme.Key;
        _ = _state.SaveSettingsAsync();
    }

    private void ThemeService_ThemeChanging(object? sender, EventArgs e)
    {
        if (!IsLoaded || WindowRoot.ActualWidth < 2 || WindowRoot.ActualHeight < 2)
        {
            return;
        }

        ThemeTransitionLayer.Children.Clear();
        _themeSnapshot = null;
        var bitmap = CaptureWindow();
        if (bitmap is null)
        {
            return;
        }

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            Opacity = 1
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        ThemeTransitionLayer.Children.Add(image);
        _themeSnapshot = image;
    }

    private BitmapSource? CaptureWindow()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // Half-resolution is plenty for a sub-second transition and cuts the
            // synchronous render cost by roughly 4x.
            const double scale = 0.5;
            var width = (int)Math.Ceiling(WindowRoot.ActualWidth * dpi.DpiScaleX * scale);
            var height = (int)Math.Ceiling(WindowRoot.ActualHeight * dpi.DpiScaleY * scale);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            {
                return null;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX * scale,
                96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
            bitmap.Render(WindowRoot);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyDwmBorder(IntPtr handle)
    {
        try
        {
            if (FindResource("BgBrush") is SolidColorBrush background)
            {
                // COLORREF is 0x00BBGGRR. Matching the window border to the
                // theme hides the remaining rectangular 1px DWM frame.
                var color = background.Color;
                var colorRef = color.R | color.G << 8 | color.B << 16;
                DwmSetWindowAttribute(handle, 34, ref colorRef, sizeof(int));
            }
        }
        catch
        {
        }
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        UpdateThemeToggle();
        try
        {
            ApplyDwmBorder(new WindowInteropHelper(this).Handle);
        }
        catch
        {
        }
        var oldImage = _themeSnapshot;
        _themeSnapshot = null;
        if (oldImage is null)
        {
            return;
        }

        var center = new Point(ThemeTransitionLayer.ActualWidth - 82, 30);
        try
        {
            center = ThemeToggleButton.TranslatePoint(
                new Point(ThemeToggleButton.ActualWidth / 2, ThemeToggleButton.ActualHeight / 2),
                ThemeTransitionLayer);
        }
        catch
        {
        }

        var dx = Math.Max(center.X, Math.Max(0, ThemeTransitionLayer.ActualWidth - center.X));
        var dy = Math.Max(center.Y, Math.Max(0, ThemeTransitionLayer.ActualHeight - center.Y));
        var radius = Math.Sqrt(dx * dx + dy * dy) + 32;
        var hole = new EllipseGeometry(center, 0, 0);
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, WindowRoot.ActualWidth, WindowRoot.ActualHeight)), hole);
        oldImage.Clip = mask;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(620);
        var radiusX = new DoubleAnimation(0, radius, duration) { EasingFunction = ease };
        var radiusY = new DoubleAnimation(0, radius, duration) { EasingFunction = ease };
        radiusX.Completed += (_, _) =>
        {
            ThemeTransitionLayer.Children.Remove(oldImage);
            oldImage.Clip = null;
        };
        try
        {
            hole.BeginAnimation(EllipseGeometry.RadiusXProperty, radiusX);
            hole.BeginAnimation(EllipseGeometry.RadiusYProperty, radiusY);
        }
        catch
        {
            ThemeTransitionLayer.Children.Remove(oldImage);
        }
    }

    private void UpdateThemeToggle()
    {
        if (ThemeGlyph is null || ThemeLabel is null) return;
        ThemeGlyph.Text = ThemeService.IsDark ? "\uE708" : "\uE706";
        ThemeLabel.Text = ThemeService.IsDark ? "深色" : "浅色";
        ThemeToggleButton.ToolTip = ThemeService.IsDark ? "切换到浅色模式" : "切换到深色模式";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AccountChip_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPopup.IsOpen)
        {
            AccountPopup.IsOpen = false;
            return;
        }

        AccountPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (AccountMenuScale is null) return;
            BackdropService.ApplyToBorder(AccountMenuPanel, AccountGlassBorder);
            RoundedMask.Apply(AccountMenuPanel, 16);
            AccountMenuScale.ScaleX = 0.94;
            AccountMenuScale.ScaleY = 0.94;
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
            AccountMenuScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            AccountMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            AccountMenuPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }), DispatcherPriority.Render);
    }

    private void AccountSettings_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        Navigate("settings");
    }

    private void AccountTheme_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        ThemeToggle_Click(sender, e);
    }

    private void AccountUpdate_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        var window = new UpdateWindow(_services) { Owner = this };
        window.ShowDialog();
    }

    private void AccountData_Click(object sender, RoutedEventArgs e)
    {
        AccountPopup.IsOpen = false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Paths.AppRoot}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开数据目录：" + ex.Message, true);
        }
    }

    private void ShowLaunchOverlay_Click(object sender, RoutedEventArgs e) => _state.ShowActiveGameOverlay();

    private void DismissLaunchOverlay_Click(object sender, RoutedEventArgs e) => _state.DismissLaunchOverlay();

    private void StopGame_Click(object sender, RoutedEventArgs e) => _state.StopGame();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var previousState = _lastWindowState;
        _lastWindowState = WindowState;
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }

        if (WindowRoot is not null)
        {
            // WindowChrome reports a maximized window 8px outside the work area.
            // Inset the rounded shell so the right/bottom content is not clipped.
            WindowRoot.Margin = WindowState == WindowState.Maximized
                ? new Thickness(8)
                : new Thickness(0);
            WindowRoot.CornerRadius = WindowState == WindowState.Maximized
                ? new CornerRadius(0)
                : new CornerRadius(10);
            ApplyRoundedRegion();
        }

        if (_startupComplete && previousState == WindowState.Minimized &&
            WindowState != WindowState.Minimized && !_stateAnimationRunning)
        {
            _ = PlayRestoreAnimation();
        }
    }

    private async Task PlayRestoreAnimation()
    {
        if (_stateAnimationRunning) return;
        _stateAnimationRunning = true;
        try
        {
            await AnimateWindowShell(0.94, 1, 0, 1, 220,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 });
        }
        finally
        {
            _stateAnimationRunning = false;
        }
    }

    private static void ApplyPanelClip(FrameworkElement panel, double radius)
    {
        if (panel.ActualWidth < 1 || panel.ActualHeight < 1) return;
        panel.Clip = new RectangleGeometry(
            new Rect(0, 0, panel.ActualWidth, panel.ActualHeight), radius, radius);
    }

    private void ApplyRoundedRegion()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || ActualWidth < 2 || ActualHeight < 2) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            var radius = WindowState == WindowState.Maximized
                ? 0
                : (int)Math.Round(10 * dpi.DpiScaleX * 2);
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
            SetWindowRgn(handle, region, true);
        }
        catch
        {
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = ThemeService.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var corner = 2;
            DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
            ApplyDwmBorder(handle);
            ApplyRoundedRegion();
        }
        catch
        {
        }
    }

    private void ShowToast(string message, bool error)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ToastText.Text = message;
            ToastDot.Fill = (Brush)FindResource(error ? "DangerBrush" : "AccentBrush");
            ToastBorder.Visibility = Visibility.Visible;
            ToastBorder.Opacity = 0;
            ToastBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }));
    }

    private void HideToast()
    {
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        animation.Completed += (_, _) => ToastBorder.Visibility = Visibility.Collapsed;
        ToastBorder.BeginAnimation(OpacityProperty, animation);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            CleanupClosing();
            return;
        }

        // Defer the real close until the exit animation has played.
        e.Cancel = true;
        if (_closeAnimationRunning)
        {
            return;
        }

        _closeAnimationRunning = true;
        try
        {
            if (WindowState == WindowState.Normal)
            {
                _state.Settings.WindowWidth = Width;
                _state.Settings.WindowHeight = Height;
            }

            _services.Settings.SaveSynchronously();
        }
        catch
        {
        }

        _toastTimer.Stop();
        PlayCloseAnimation();
    }

    private void PlayCloseAnimation()
    {
        if (_rootScale is not null)
        {
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                });
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.985, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                });
        }

        if (_rootTranslate is not null)
        {
            _rootTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                });
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => CompleteClose();
        WindowRoot.BeginAnimation(OpacityProperty, fade);

        if (SidebarRoot is not null)
        {
            SidebarRoot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)));
        }

        if (PageTransitionRoot is not null)
        {
            PageTransitionRoot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)));
        }

        // Safety net: never leave an unclosable window if an animation is interrupted.
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            CompleteClose();
        };
        fallback.Start();
    }

    private void CompleteClose()
    {
        if (_allowClose) return;
        _allowClose = true;
        _closeAnimationRunning = false;
        CleanupClosing();
        Close();
    }

    private bool _closingCleanupDone;

    private void CleanupClosing()
    {
        if (_closingCleanupDone) return;
        _closingCleanupDone = true;
        _state.ToastRequested -= ShowToast;
        _state.PropertyChanged -= State_PropertyChanged;
        ThemeService.ThemeChanging -= ThemeService_ThemeChanging;
        ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
        _toastTimer.Stop();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
