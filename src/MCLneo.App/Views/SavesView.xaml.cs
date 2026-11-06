using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MCLneo.App.Services;
using MCLneo.Core.Worlds;

namespace MCLneo.App.Views;

public partial class SavesView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private bool _initialized;
    private bool _busy;

    public SavesView(AppServices services, AppState state)
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
            Dispatcher.BeginInvoke(new Action(() => RefreshInstanceUi(load: true)));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshInstanceUi(load: true);
        await Task.CompletedTask;
    }

    private void InstanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) RefreshInstanceUi(load: true);
    }

    private void RefreshInstanceUi(bool load)
    {
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            NoInstancePanel.Visibility = Visibility.Visible;
            WorldList.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            HintText.Text = "未选择游戏";
            return;
        }

        NoInstancePanel.Visibility = Visibility.Collapsed;
        WorldList.Visibility = Visibility.Visible;
        HintText.Text = $"存档目录：{instance.SavesDirectory}";
        if (load)
        {
            _ = LoadWorldsAsync();
        }
    }

    private async Task LoadWorldsAsync()
    {
        var instance = _state.SelectedInstance;
        if (instance is null) return;
        try
        {
            var worlds = await _services.Worlds.GetWorldsAsync(instance);
            WorldList.ItemsSource = worlds;
            EmptyPanel.Visibility = worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取存档失败", ex);
            _state.Notify("读取存档失败：" + ex.Message, true);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadWorldsAsync();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: WorldInfo world }) return;
        var instance = _state.SelectedInstance;
        if (instance is null) return;
        var directory = Path.Combine(AppState.ExportDirectory, "存档");
        Directory.CreateDirectory(directory);
        var dialog = new SaveFileDialog
        {
            Title = "导出存档",
            Filter = "Minecraft 存档备份 (*.zip)|*.zip",
            FileName = WorldService.CreateBackupName(world) ?? "world.zip",
            InitialDirectory = directory
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _busy = true;
        ShowBusy("正在导出存档", $"打包「{world.Name}」…");
        try
        {
            if (File.Exists(dialog.FileName))
            {
                File.Delete(dialog.FileName);
            }

            var progress = new Progress<double>(value =>
            {
                BusyProgress.Value = value;
                BusyPercent.Text = value.ToString("P0");
            });
            await _services.Worlds.ExportAsync(world, dialog.FileName, progress);
            _state.Notify("存档已导出：" + Path.GetFileName(dialog.FileName), false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("导出存档失败", ex);
            _state.Notify("导出失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
            HideBusy();
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: WorldInfo world }) return;
        var instance = _state.SelectedInstance;
        if (instance is null) return;
        _busy = true;
        ShowBusy("正在备份存档", $"创建「{world.Name}」的时间点备份…");
        try
        {
            var progress = new Progress<double>(value =>
            {
                BusyProgress.Value = value;
                BusyPercent.Text = value.ToString("P0");
            });
            var path = await _services.Worlds.BackupAsync(instance, world, progress);
            _state.Notify("存档已备份：" + Path.GetFileName(path), false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("备份存档失败", ex);
            _state.Notify("备份失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
            HideBusy();
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            _state.Notify("请先选择一个游戏", true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入 Minecraft 存档",
            Filter = "Minecraft 存档备份 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _busy = true;
        ShowBusy("正在导入存档", "正在校验压缩包…");
        try
        {
            var progress = new Progress<double>(value =>
            {
                BusyProgress.Value = value;
                BusyPercent.Text = value.ToString("P0");
            });
            var world = await _services.Worlds.ImportAsync(dialog.FileName, instance, progress);
            _state.Notify($"存档「{world.Name}」已导入", false);
            await LoadWorldsAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Error("导入存档失败", ex);
            _state.Notify("导入失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
            HideBusy();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldInfo world }) return;
        if (!MessageDialog.Confirm(Window.GetWindow(this), "删除存档",
                $"将把「{world.Name}」移动到 MCLneo 的回收目录。\n世界文件不会立即永久删除，但游戏内将不再显示它。",
                "移到回收目录", danger: true))
        {
            return;
        }

        try
        {
            await _services.Worlds.DeleteAsync(world);
            _state.Notify("存档已移到回收目录", false);
            await LoadWorldsAsync();
        }
        catch (Exception ex)
        {
            _state.Notify("删除失败：" + ex.Message, true);
        }
    }

    private void Datapacks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldInfo world }) return;
        var directory = Path.Combine(world.Directory, "datapacks");
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开数据包目录：" + ex.Message, true);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldInfo world }) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{world.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开目录：" + ex.Message, true);
        }
    }

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
