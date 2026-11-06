using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using MCLneo.App.Services;
using MCLneo.Core.Models;

namespace MCLneo.App.Views;

public partial class InstancesView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly ICollectionView _view;
    private bool _loadedOnce;

    public InstancesView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        _view = CollectionViewSource.GetDefaultView(state.Instances);
        _view.Filter = FilterInstance;
        InstanceList.ItemsSource = _view;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce && _state.IsInitialized)
        {
            // Still refresh; another page may have installed an instance.
        }

        _loadedOnce = true;
        await _state.RefreshInstancesAsync();
        _view.Refresh();
    }

    private bool FilterInstance(object item)
    {
        if (item is not GameInstance instance)
        {
            return false;
        }

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return instance.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               instance.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               instance.MinecraftVersion.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _state.RefreshInstancesAsync();
        _view.Refresh();
        _state.Notify("游戏列表已刷新", false);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateInstanceDialog(_services, _state) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await _state.RefreshInstancesAsync(dialog.Result.Id);
            _state.SelectedInstance = _state.Instances.FirstOrDefault(i => i.Id == dialog.Result.Id) ?? dialog.Result;
            _view.Refresh();
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
        _state.SelectedInstance = instance;
        await _state.LaunchAsync(instance);
    }

    private void InstanceCard_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: GameInstance instance })
        {
            _state.SelectedInstance = instance;
            _ = _state.LaunchAsync(instance);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
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

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
        var owner = Window.GetWindow(this);
        var name = InputDialog.Show(owner, "重命名游戏", "给这个游戏取一个更容易记住的名字。", instance.Name);
        if (string.IsNullOrWhiteSpace(name) || name == instance.Name) return;
        await _services.Instances.RenameAsync(instance, name);
        await _state.RefreshInstancesAsync(instance.Id);
        _state.Notify("游戏已重命名", false);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
        var owner = Window.GetWindow(this);
        if (!MessageDialog.Confirm(owner, "删除游戏",
                $"将把「{instance.Name}」移动到 MCLneo 的回收目录。\n不会立即永久删除，但你将无法在启动器中看到它。",
                "移到回收目录", danger: true))
        {
            return;
        }

        await _services.Instances.DeleteAsync(instance);
        await _state.RefreshInstancesAsync();
        _state.Notify("游戏已移到回收目录", false);
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
        var window = new LogViewerWindow(_services, instance) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
        ShowBusy("正在修复游戏", "检查基础版本、运行库与资源索引…");
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                BusyDetail.Text = $"{p.Stage} · {p.Detail}";
                BusyProgress.Value = Math.Clamp(p.Fraction, 0, 1);
                BusyPercent.Text = p.Fraction.ToString("P0");
            });
            await _services.Installer.RepairAsync(instance, progress);
            _state.Notify("游戏修复完成", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("修复游戏失败", ex);
            _state.Notify("修复失败：" + ex.Message, true);
            MessageDialog.Show(Window.GetWindow(this), "修复失败", ex.Message);
        }
        finally
        {
            HideBusy();
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var instance = ResolveInstance(sender);
        if (instance is null) return;
        Directory.CreateDirectory(AppState.ExportDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "导出 MCLneo 游戏",
            Filter = "MCLneo 游戏包 (*.mclneopack)|*.mclneopack|Zip 压缩包 (*.zip)|*.zip",
            FileName = $"{instance.Id}-{DateTime.Now:yyyyMMdd}.mclneopack",
            InitialDirectory = AppState.ExportDirectory
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ShowBusy("正在导出游戏", "正在打包模组、配置与存档…");
        try
        {
            var progress = new Progress<double>(value =>
            {
                BusyProgress.Value = value;
                BusyPercent.Text = $"{value:P0}";
            });
            await _services.Instances.ExportAsync(instance, dialog.FileName, true, progress);
            _state.Notify("游戏已导出：" + Path.GetFileName(dialog.FileName), false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("导出游戏失败", ex);
            _state.Notify("导出失败：" + ex.Message, true);
        }
        finally
        {
            HideBusy();
        }
    }

    private async void ImportExternal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择已有的 .minecraft 或 PCL2 游戏目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var root = dialog.FolderName;
        if (!Directory.Exists(Path.Combine(root, "versions")))
        {
            _state.Notify("该目录下没有 versions 文件夹，可能不是 Minecraft 游戏目录", true);
            return;
        }

        _services.Settings.Current.GameRoot = Path.GetFullPath(root);
        await _state.SaveSettingsAsync();
        await _state.RefreshInstancesAsync();
        _state.Notify("已切换到该游戏目录，已有游戏版本已导入列表", false);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 MCLneo 游戏",
            Filter = "MCLneo 游戏包 (*.mclneopack;*.zip)|*.mclneopack;*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ShowBusy("正在导入游戏", "正在校验并解压游戏包…");
        try
        {
            var progress = new Progress<double>(value =>
            {
                BusyProgress.Value = value * 0.85;
                BusyPercent.Text = $"{value * 0.85:P0}";
            });
            var imported = await _services.Instances.ImportAsync(dialog.FileName, null, progress);
            ShowBusy("正在读取游戏", "正在刷新游戏库…");
            await _state.RefreshInstancesAsync(imported.Id);
            _state.SelectedInstance = _state.Instances.FirstOrDefault(i => i.Id == imported.Id);
            _state.Notify("游戏已导入", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("导入游戏失败", ex);
            _state.Notify("导入失败：" + ex.Message, true);
        }
        finally
        {
            HideBusy();
        }
    }

    private static GameInstance? ResolveInstance(object sender) =>
        (sender as FrameworkElement)?.Tag as GameInstance ?? (sender as FrameworkElement)?.DataContext as GameInstance;

    private void ShowBusy(string title, string detail)
    {
        BusyTitle.Text = title;
        BusyDetail.Text = detail;
        BusyProgress.Value = 0;
        BusyPercent.Text = "0%";
        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;
}
