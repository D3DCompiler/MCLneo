using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MCLneo.App.Services;
using MCLneo.Core;
using MCLneo.Core.Accounts;
using MCLneo.Core.Rooms;

namespace MCLneo.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private List<JavaChoice> _javaChoices = new();
    private bool _initialized;

    public SettingsView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        _initialized = true;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _services.Settings.Current;
        PlayerNameBox.Text = settings.LastPlayerName;
        GameRootBox.Text = settings.GameRoot;
        PeerBox.Text = settings.EasyTierPeer ?? "";
        CorePathBox.Text = settings.EasyTierCorePath ?? "";

        MemoryBox.Items.Clear();
        var memories = new[] { 2048, 3072, 4096, 6144, 8192, 12288, 16384 };
        foreach (var memory in memories.Append(settings.MemoryMb).Distinct().OrderBy(m => m))
        {
            MemoryBox.Items.Add($"{memory} MB");
        }

        MemoryBox.SelectedItem = $"{settings.MemoryMb} MB";

        SourceBox.Items.Clear();
        SourceBox.Items.Add("BMCLAPI 镜像（推荐）");
        SourceBox.Items.Add("Mojang 官方");
        SourceBox.SelectedIndex = settings.DownloadSource == DownloadSource.Bmclapi ? 0 : 1;

        ThreadBox.Items.Clear();
        foreach (var threads in new[] { 4, 8, 12, 16 }.Append(settings.MaxDownloadThreads).Distinct().OrderBy(t => t))
        {
            ThreadBox.Items.Add(threads.ToString());
        }

        ThreadBox.SelectedItem = settings.MaxDownloadThreads.ToString();
        ThemeModeBox.Items.Clear();
        ThemeModeBox.Items.Add("深色");
        ThemeModeBox.Items.Add("浅色");
        ThemeModeBox.SelectedIndex = string.Equals(settings.ThemeMode, "light",
            StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        ThemeBox.Items.Clear();
        foreach (var theme in ThemeService.Themes)
        {
            ThemeBox.Items.Add(theme);
        }

        ThemeBox.DisplayMemberPath = nameof(ThemeService.Theme.Name);
        var accentKey = settings.AccentTheme?.ToLowerInvariant() switch
        {
            "mint" or "moss" or "cyan" or "cobalt" => "blue",
            "persimmon" or "amber" or "ochre" => "orange",
            _ => settings.AccentTheme
        };
        ThemeBox.SelectedItem = ThemeService.Themes.FirstOrDefault(t =>
            string.Equals(t.Key, accentKey, StringComparison.OrdinalIgnoreCase)) ??
            ThemeService.Themes[0];
        RebuildJavaChoices(settings.JavaPath);
        UpdateUuid();
        UpdateAccountUi();
    }

    private void UpdateAccountUi()
    {
        var active = _state.ActiveAuthlibAccount;
        AccountStatusText.Text = active is null
            ? "当前：离线账户"
            : $"当前：{active.PlayerName} @ {active.ServerDisplay}";
        ThirdPartyBadge.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        LogoutAuthlibButton.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        SkinSiteButton.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        PlayerNameBox.IsEnabled = active is null;
        UuidText.Text = active?.Uuid ?? UuidText.Text;
        _ = LoadSkinAsync();
    }

    private async Task LoadSkinAsync()
    {
        try
        {
            var path = await _services.Skins.GetSkinAsync(_state.Player, _state.ActiveAuthlibAccount?.Server);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            SkinImage.Source = bitmap;
        }
        catch
        {
        }
    }

    private void SkinSite_Click(object sender, RoutedEventArgs e)
    {
        var server = _state.ActiveAuthlibAccount?.Server;
        if (string.IsNullOrWhiteSpace(server)) return;
        try
        {
            Process.Start(new ProcessStartInfo(server) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开皮肤站：" + ex.Message, true);
        }
    }

    private void AuthlibLogin_Click(object sender, RoutedEventArgs e)
    {
        var window = new AuthlibLoginWindow(_state) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            PlayerNameBox.Text = _state.PlayerName;
            UpdateUuid();
            UpdateAccountUi();
        }
    }

    private async void AuthlibLogout_Click(object sender, RoutedEventArgs e)
    {
        await _state.LogoutAuthlibAsync();
        PlayerNameBox.Text = _state.Settings.LastPlayerName;
        UpdateUuid();
        UpdateAccountUi();
    }

    private void RebuildJavaChoices(string? selectedPath)
    {
        _javaChoices = new List<JavaChoice>
        {
            new("自动选择 / 启动时下载所需版本", "")
        };
        _javaChoices.AddRange(_state.JavaRuntimes.Select(r =>
            new JavaChoice($"{r.Display}  ·  {r.ExecutablePath}", r.ExecutablePath)));
        JavaBox.ItemsSource = _javaChoices;
        var selected = _javaChoices.FirstOrDefault(c =>
                           !string.IsNullOrWhiteSpace(selectedPath) &&
                           string.Equals(c.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                       ?? _javaChoices[0];
        JavaBox.SelectedItem = selected;
    }

    private void UpdateUuid()
    {
        try
        {
            UuidText.Text = PlayerProfile.Offline(PlayerNameBox.Text).UuidDisplay;
        }
        catch
        {
            UuidText.Text = "玩家名无效";
        }
    }

    private void PlayerName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) UpdateUuid();
    }

    private void BrowseGameRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Minecraft 游戏目录",
            InitialDirectory = Directory.Exists(GameRootBox.Text) ? GameRootBox.Text : null
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            GameRootBox.Text = dialog.FolderName;
        }
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "Java 可执行文件 (java.exe)|java.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dialog.FileName;
        if (!_javaChoices.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            _javaChoices.Add(new JavaChoice("手动选择 · " + path, path));
            JavaBox.ItemsSource = null;
            JavaBox.ItemsSource = _javaChoices;
        }

        JavaBox.SelectedItem = _javaChoices.First(c =>
            string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private void BrowseCore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 easytier-core.exe",
            Filter = "EasyTier Core (easytier-core.exe)|easytier-core.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            CorePathBox.Text = dialog.FileName;
        }
    }

    private async void ManageJava_Click(object sender, RoutedEventArgs e)
    {
        var window = new JavaManagerWindow(_services) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await _state.ReloadJavaAsync();
        var selected = (JavaBox.SelectedItem as JavaChoice)?.Path;
        RebuildJavaChoices(selected);
    }

    private async void RescanJava_Click(object sender, RoutedEventArgs e)
    {
        var selected = (JavaBox.SelectedItem as JavaChoice)?.Path;
        await _state.ReloadJavaAsync();
        RebuildJavaChoices(selected);
        _state.Notify($"已检测到 {_state.JavaRuntimes.Count} 个 Java 运行时", false);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        PlayerProfile profile;
        try
        {
            profile = PlayerProfile.Offline(PlayerNameBox.Text);
        }
        catch (Exception ex)
        {
            _state.Notify(ex.Message, true);
            return;
        }

        var root = GameRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            _state.Notify("游戏目录不能为空", true);
            return;
        }

        try
        {
            root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            _state.Notify("游戏目录不可用：" + ex.Message, true);
            return;
        }

        var peer = PeerBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(peer) && !RoomInvite.IsValidPeer(peer))
        {
            _state.Notify("中继地址格式无效，例如 tcp://public.easytier.top:11010", true);
            return;
        }

        var corePath = CorePathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(corePath) && !File.Exists(corePath))
        {
            _state.Notify("EasyTier Core 路径不存在", true);
            return;
        }

        var settings = _services.Settings.Current;
        settings.GameRoot = root;
        settings.LastPlayerName = profile.Name;
        settings.JavaPath = (JavaBox.SelectedItem as JavaChoice)?.Path ?? "";
        settings.MemoryMb = ParseLeadingInt(MemoryBox.SelectedItem?.ToString(), 4096);
        settings.DownloadSource = SourceBox.SelectedIndex == 0 ? DownloadSource.Bmclapi : DownloadSource.Official;
        settings.MaxDownloadThreads = ParseLeadingInt(ThreadBox.SelectedItem?.ToString(), 8);
        settings.EasyTierPeer = string.IsNullOrWhiteSpace(peer) ? null : peer;
        settings.EasyTierCorePath = string.IsNullOrWhiteSpace(corePath) ? null : corePath;
        settings.AccentTheme = (ThemeBox.SelectedItem as ThemeService.Theme)?.Key ?? "blue";
        settings.ThemeMode = ThemeModeBox.SelectedIndex == 1 ? "light" : "dark";

        await _state.SaveSettingsAsync();
        PlayerNameBox.Text = profile.Name;
        UpdateUuid();
    }

    private static int ParseLeadingInt(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : fallback;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var window = new HelpWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        var window = new UpdateWindow(_services) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void OpenGameRoot_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(GameRootBox.Text.Trim());
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(_services.Paths.AppRoot);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开目录：" + ex.Message, true);
        }
    }

    private sealed record JavaChoice(string Display, string Path)
    {
        public override string ToString() => Display;
    }
}
