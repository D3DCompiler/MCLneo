using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MCLneo.App.Services;
using MCLneo.Core.Models;
using MCLneo.Core.Mods;

namespace MCLneo.App.Views;

public partial class ModpacksView : UserControl
{
    private const int PageSize = 20;
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly Action<string> _navigate;
    private readonly List<ModrinthProject> _results = new();
    private bool _loading;
    private bool _busy;
    private int _offset;
    private int _total;

    public ModpacksView(AppServices services, AppState state, Action<string> navigate)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        _navigate = navigate;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            await SearchAsync(reset: true);
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(reset: true);

    private async void InstallFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 .mrpack 整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|Zip 压缩包 (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var defaultName = Path.GetFileNameWithoutExtension(dialog.FileName);
        var name = InputDialog.Show(Window.GetWindow(this), "安装本地整合包",
            "为新游戏取一个名字。", defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        _busy = true;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyStage.Text = "准备安装本地整合包…";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                BusyStage.Text = $"{p.Stage} · {p.Detail}";
                BusyProgress.Value = Math.Clamp(p.Fraction, 0, 1);
                BusyPercent.Text = p.Fraction.ToString("P0");
            });
            var instance = await _services.Modpacks.InstallArchiveAsync(
                dialog.FileName, defaultName, name, progress);
            await _state.RefreshInstancesAsync(instance.Id);
            _state.SelectedInstance = _state.Instances.FirstOrDefault(i => i.Id == instance.Id) ?? instance;
            _state.Notify("本地整合包安装完成", false);
            _navigate("instances");
        }
        catch (Exception ex)
        {
            _services.Log.Error("安装本地整合包失败", ex);
            MessageDialog.Show(Window.GetWindow(this), "安装失败", ex.Message);
        }
        finally
        {
            _busy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync(reset: true);
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await SearchAsync(reset: false);

    private async Task SearchAsync(bool reset)
    {
        if (_loading || _busy) return;
        _loading = true;
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        if (reset)
        {
            _offset = 0;
            _total = 0;
            _results.Clear();
        }

        try
        {
            var response = await _services.Modrinth.SearchAsync(
                SearchBox.Text.Trim(), "modpack", null, null, _offset, PageSize,
                string.IsNullOrWhiteSpace(SearchBox.Text) ? "downloads" : "relevance");
            _total = response.TotalHits;
            _results.AddRange(response.Hits);
            _offset += response.Hits.Count;
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _results.ToList();
            ResultStatus.Text = $"{_total:N0} 个整合包";
            LoadMoreButton.Visibility = _results.Count < _total ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _services.Log.Error("搜索整合包失败", ex);
            EmptyText.Text = "搜索失败：" + ex.Message;
            EmptyPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModrinthProject project } || _busy)
        {
            return;
        }

        var name = InputDialog.Show(Window.GetWindow(this), "安装整合包",
            $"为「{project.Title}」创建独立游戏。\n整合包会安装自己的加载器、模组和配置。",
            project.Title);
        if (string.IsNullOrWhiteSpace(name)) return;

        _busy = true;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyStage.Text = "准备安装…";
        BusyProgress.Value = 0;
        BusyPercent.Text = "0%";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                BusyStage.Text = $"{p.Stage} · {p.Detail}";
                BusyProgress.Value = Math.Clamp(p.Fraction, 0, 1);
                BusyPercent.Text = p.Fraction.ToString("P0");
            });
            var instance = await _services.Modpacks.InstallAsync(project, name, progress);
            await _state.RefreshInstancesAsync(instance.Id);
            _state.SelectedInstance = _state.Instances.FirstOrDefault(i => i.Id == instance.Id) ?? instance;
            _state.Notify($"整合包「{project.Title}」安装完成", false);
            BusyOverlay.Visibility = Visibility.Collapsed;
            _navigate("instances");
        }
        catch (Exception ex)
        {
            _services.Log.Error("安装整合包失败", ex);
            BusyStage.Text = "安装失败：" + ex.Message;
            _state.Notify("整合包安装失败：" + ex.Message, true);
            MessageDialog.Show(Window.GetWindow(this), "整合包安装失败", ex.Message);
        }
        finally
        {
            _busy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }
}
