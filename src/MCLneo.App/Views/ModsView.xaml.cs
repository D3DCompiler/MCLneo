using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MCLneo.App.Services;
using MCLneo.Core;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;
using MCLneo.Core.Mods;

namespace MCLneo.App.Views;

public partial class ModsView : UserControl
{
    private const int PageSize = 20;
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly List<ModrinthProject> _results = new();
    private bool _initialized;
    private bool _loading;
    private bool _showDiscover = true;
    private bool _busy;
    private int _offset;
    private int _totalHits;
    private string _projectType = "mod";

    public ModsView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        _initialized = true;
        Loaded += OnLoaded;
        _state.PropertyChanged += State_PropertyChanged;
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.SelectedInstance))
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshInstanceUi()));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshInstanceUi();
        if (_state.SelectedInstance is not null && _results.Count == 0 && _showDiscover)
        {
            await SearchAsync(reset: true);
        }
    }

    private void InstanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        RefreshInstanceUi();
    }

    private void RefreshInstanceUi()
    {
        if (NoInstancePanel is null) return;
        var instance = _state.SelectedInstance;
        var hasInstance = instance is not null;
        NoInstancePanel.Visibility = hasInstance ? Visibility.Collapsed : Visibility.Visible;
        if (!hasInstance)
        {
            DiscoverPanel.Visibility = Visibility.Collapsed;
            InstalledPanel.Visibility = Visibility.Collapsed;
            InstanceHint.Text = "未选择游戏";
            return;
        }

        DiscoverPanel.Visibility = _showDiscover ? Visibility.Visible : Visibility.Collapsed;
        InstalledPanel.Visibility = _showDiscover ? Visibility.Collapsed : Visibility.Visible;
        InstanceHint.Text = $"目标：{instance!.Name} · Minecraft {instance.MinecraftVersion} · {instance.DisplayLoader}";
        var vanillaMods = _projectType == "mod" && instance.Loader == LoaderKind.Vanilla;
        WarningPanel.Visibility = vanillaMods ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = vanillaMods
            ? "当前游戏是原版，原版无法加载模组。请创建一个 Fabric/Quilt 游戏，或改用资源包/光影。"
            : "";
        if (!_showDiscover)
        {
            _ = LoadInstalledAsync();
        }
    }

    private void ContentTab_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _showDiscover = TabDiscover.IsChecked == true;
        if (_state.SelectedInstance is null)
        {
            RefreshInstanceUi();
            return;
        }

        DiscoverPanel.Visibility = _showDiscover ? Visibility.Visible : Visibility.Collapsed;
        InstalledPanel.Visibility = _showDiscover ? Visibility.Collapsed : Visibility.Visible;
        if (_showDiscover)
        {
            if (_results.Count == 0) _ = SearchAsync(reset: true);
        }
        else
        {
            _ = LoadInstalledAsync();
        }
    }

    private void ProjectType_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _projectType = TabMods.IsChecked == true ? "mod" : TabResourcePacks.IsChecked == true ? "resourcepack" : "shader";
        _results.Clear();
        _offset = 0;
        _totalHits = 0;
        ProjectList.ItemsSource = null;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ResultStatus.Text = "";
        RefreshInstanceUi();
        if (_showDiscover && _state.SelectedInstance is not null)
        {
            _ = SearchAsync(reset: true);
        }
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            _state.Notify("请先选择一个游戏", true);
            return;
        }

        if (_projectType == "mod" && instance.Loader == LoaderKind.Vanilla)
        {
            _state.Notify("原版游戏无法加载模组，请先创建 Fabric/Quilt 游戏", true);
            return;
        }

        var filter = _projectType switch
        {
            "mod" => "模组文件 (*.jar)|*.jar|所有文件 (*.*)|*.*",
            "resourcepack" => "资源包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            _ => "光影包 (*.zip)|*.zip|所有文件 (*.*)|*.*"
        };
        var dialog = new OpenFileDialog
        {
            Title = _projectType == "mod" ? "导入本地模组" : "导入本地资源",
            Filter = filter,
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var directory = ModrinthService.TargetDirectory(instance, _projectType);
            Directory.CreateDirectory(directory);
            var imported = 0;
            foreach (var file in dialog.FileNames)
            {
                var name = PathSafety.SafeFileName(Path.GetFileName(file), "download.jar");
                File.Copy(file, Path.Combine(directory, name), true);
                imported++;
            }

            _state.Notify($"已导入 {imported} 个文件", false);
            _showDiscover = false;
            TabInstalled.IsChecked = true;
            DiscoverPanel.Visibility = Visibility.Collapsed;
            InstalledPanel.Visibility = Visibility.Visible;
            _ = LoadInstalledAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Error("导入本地资源失败", ex);
            _state.Notify("导入失败：" + ex.Message, true);
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync(reset: true);
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync(reset: true);
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync(reset: false);
    }

    private async Task SearchAsync(bool reset)
    {
        if (_loading || _busy) return;
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            RefreshInstanceUi();
            return;
        }

        if (_projectType == "mod" && instance.Loader == LoaderKind.Vanilla)
        {
            RefreshInstanceUi();
            return;
        }

        _loading = true;
        DiscoverLoading.Visibility = Visibility.Visible;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        if (reset)
        {
            _offset = 0;
            _totalHits = 0;
            _results.Clear();
        }

        try
        {
            var loader = _projectType == "mod" ? ModrinthService.LoaderId(instance.Loader) : null;
            var response = await _services.Modrinth.SearchAsync(
                SearchBox.Text.Trim(),
                _projectType,
                instance.MinecraftVersion,
                loader,
                _offset,
                PageSize,
                string.IsNullOrWhiteSpace(SearchBox.Text) ? "downloads" : "relevance");
            _totalHits = response.TotalHits;
            _results.AddRange(response.Hits);
            _offset += response.Hits.Count;
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _results.ToList();
            NoResultsPanel.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreButton.Visibility = _results.Count < _totalHits ? Visibility.Visible : Visibility.Collapsed;
            ResultStatus.Text = $"{_totalHits:N0} 个结果 · 适用于 Minecraft {instance.MinecraftVersion}" +
                                (_projectType == "mod" ? $" / {instance.DisplayLoader}" : "");
        }
        catch (Exception ex)
        {
            _services.Log.Error("搜索 Modrinth 失败", ex);
            WarningPanel.Visibility = Visibility.Visible;
            WarningText.Text = "搜索失败：" + ex.Message;
            ResultStatus.Text = "请求失败，请稍后重试。";
        }
        finally
        {
            _loading = false;
            DiscoverLoading.Visibility = Visibility.Collapsed;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModrinthProject project }) return;
        var instance = _state.SelectedInstance;
        if (instance is null || _busy) return;
        if (_projectType == "mod" && instance.Loader == LoaderKind.Vanilla)
        {
            _state.Notify("原版游戏无法安装模组，请先创建 Fabric 游戏", true);
            return;
        }

        _busy = true;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyStatus.Text = $"正在准备安装 {project.Title}…";
        try
        {
            var status = new Progress<string>(text => BusyStatus.Text = text);
            var result = await _services.Modrinth.InstallAsync(project, instance, _projectType, status);
            BusyOverlay.Visibility = Visibility.Collapsed;
            var extra = result.Warnings.Count > 0 ? $"，{result.Warnings.Count} 个依赖需手动处理" : "";
            _state.Notify($"已安装 {project.Title}（{result.InstalledFiles.Count} 个文件）{extra}", false);
            if (result.Warnings.Count > 0)
            {
                MessageDialog.Show(Window.GetWindow(this), "依赖提示",
                    string.Join("\n", result.Warnings) + "\n\n请到项目页面手动下载这些依赖。");
            }

            if (!_showDiscover)
            {
                await LoadInstalledAsync();
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error($"安装项目失败：{project.Title}", ex);
            _state.Notify("安装失败：" + ex.Message, true);
            MessageDialog.Show(Window.GetWindow(this), "安装失败", ex.Message);
        }
        finally
        {
            _busy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadInstalledAsync()
    {
        var instance = _state.SelectedInstance;
        if (instance is null) return;
        try
        {
            var installed = await _services.Modrinth.GetInstalledAsync(instance, _projectType);
            InstalledList.ItemsSource = installed;
            InstalledEmpty.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取已安装项目失败", ex);
            _state.Notify("读取已安装项目失败：" + ex.Message, true);
        }
    }

    private async void ToggleInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledMod mod }) return;
        try
        {
            ModrinthService.SetEnabled(mod, !mod.Enabled);
            await LoadInstalledAsync();
        }
        catch (Exception ex)
        {
            _state.Notify("切换失败，游戏可能正在占用文件：" + ex.Message, true);
        }
    }

    private async void DeleteInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledMod mod }) return;
        if (!MessageDialog.Confirm(Window.GetWindow(this), "删除项目",
                $"确定删除「{mod.DisplayName}」吗？\n这会直接删除文件，而不是移到回收站。", "删除", danger: true))
        {
            return;
        }

        try
        {
            ModrinthService.Delete(mod);
            await LoadInstalledAsync();
        }
        catch (Exception ex)
        {
            _state.Notify("删除失败：" + ex.Message, true);
        }
    }

    private void OpenInstalledFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledMod mod }) return;
        var directory = Path.GetDirectoryName(mod.FilePath);
        if (directory is null || !Directory.Exists(directory)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开目录：" + ex.Message, true);
        }
    }
}
