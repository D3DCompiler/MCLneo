using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MCLneo.App.Services;
using MCLneo.Core;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Minecraft;
using MCLneo.Core.Models;
using MCLneo.Core.Mods;

namespace MCLneo.App.Views;

public partial class CreateInstanceDialog : Window
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly string? _preselectedVersion;
    private List<MinecraftVersionInfo> _allVersions = new();
    private bool _nameEdited;
    private bool _loading;
    private bool _installing;
    private bool _closeRequested;
    private readonly List<string> _selectedMods = new();
    private readonly List<ModrinthProject> _selectedProjects = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _loaderVersionsCts;

    public CreateInstanceDialog(AppServices services, AppState state, string? preselectedVersion = null)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        _preselectedVersion = preselectedVersion;
        LoaderVanilla.IsChecked = true;
        SourceText.Text = services.Settings.Current.DownloadSource == DownloadSource.Bmclapi
            ? "下载源：BMCLAPI（国内镜像）"
            : "下载源：Mojang 官方";
        NameBox.Text = "Minecraft 世界";
        _nameEdited = false;
        UpdateLoaderUi();
        UpdatePathPreview();
    }

    public GameInstance? Result { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        await LoadVersionsAsync(force: false);
    }

    private async Task LoadVersionsAsync(bool force)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        InstallButton.IsEnabled = false;
        VersionHint.Text = "正在读取版本清单…";
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            var manifest = await _services.Catalog.GetManifestAsync(_services.Settings.Current.DownloadSource, force);
            _allVersions = manifest.Versions.ToList();
            ApplyVersionFilter(_preselectedVersion);
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取版本清单失败", ex);
            ShowError("无法读取版本清单：" + ex.Message);
            VersionHint.Text = "读取失败，可以点击右侧刷新按钮重试。";
        }
        finally
        {
            _loading = false;
            InstallButton.IsEnabled = !_installing;
        }
    }

    private void ApplyVersionFilter(string? preferredId = null)
    {
        var includeSnapshots = SnapshotCheck.IsChecked == true;
        var selectedId = preferredId ?? (VersionBox.SelectedItem as MinecraftVersionInfo)?.Id;
        var versions = _allVersions
            .Where(v => includeSnapshots || v.Type == "release")
            .OrderByDescending(v => v.ReleaseTime)
            .ToList();
        VersionBox.ItemsSource = versions;
        var selected = versions.FirstOrDefault(v => string.Equals(v.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                       ?? versions.FirstOrDefault(v => v.IsLatestRelease)
                       ?? versions.FirstOrDefault();
        VersionBox.SelectedItem = selected;
        VersionHint.Text = $"{versions.Count} 个{(includeSnapshots ? "版本" : "正式版")} · " +
                           (selected is null ? "没有可用版本" : $"最新选中 {selected.Id}");
        if (selected is not null && !_nameEdited)
        {
            NameBox.Text = $"Minecraft {selected.Id}";
        }
    }

    private void Snapshot_Changed(object sender, RoutedEventArgs e)
    {
        if (_allVersions.Count > 0)
        {
            ApplyVersionFilter();
        }
    }

    private async void ReloadVersions_Click(object sender, RoutedEventArgs e)
    {
        await LoadVersionsAsync(force: true);
    }

    private void VersionBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_nameEdited && VersionBox.SelectedItem is MinecraftVersionInfo version)
        {
            NameBox.Text = $"Minecraft {version.Id}";
        }

        if (!_installing)
        {
            _selectedProjects.Clear();
            UpdateModsSummary();
            _ = RefreshLoaderVersionsAsync();
        }
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (NameBox.IsKeyboardFocusWithin)
        {
            _nameEdited = true;
        }

        UpdatePathPreview();
    }

    private void UpdatePathPreview()
    {
        if (PathPreview is null || _services is null)
        {
            return;
        }

        var safe = PathSafety.SafeFileName(NameBox?.Text ?? "", "New-Instance");
        var fullPath = Path.Combine(_services.Settings.Current.GameRoot, "versions", safe);
        PathPreview.Text = $"版本目录：versions\\{safe}";
        PathPreview.ToolTip = fullPath;
    }

    private void Loader_Changed(object sender, RoutedEventArgs e)
    {
        if (LoaderVersionBox is null)
        {
            return;
        }

        UpdateLoaderUi();
    }

    private void UpdateLoaderUi()
    {
        var modded = LoaderFabric?.IsChecked == true || LoaderQuilt?.IsChecked == true ||
                     LoaderForge?.IsChecked == true || LoaderNeoForge?.IsChecked == true;
        LoaderVersionBox.IsEnabled = modded;
        LoaderVersionLabel.Text = modded ? "加载器版本（可选）" : "加载器版本";
        if (ModsPanel is not null)
        {
            ModsPanel.Visibility = modded ? Visibility.Visible : Visibility.Collapsed;
            if (modded)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => ModsPanel.BringIntoView()));
            }
        }

        if (!modded)
        {
            LoaderVersionBox.Items.Clear();
            LoaderVersionBox.Text = "";
            LoaderVersionHint.Text = "原版游戏不需要加载器版本。";
        }

        _selectedProjects.Clear();
        UpdateModsSummary();
        _ = RefreshLoaderVersionsAsync();
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        if (LoaderVersionBox is null || VersionBox is null) return;
        var version = VersionBox.SelectedItem as MinecraftVersionInfo;
        var loader = SelectedLoader;
        if (version is null || loader == LoaderKind.Vanilla || _installing)
        {
            return;
        }

        _loaderVersionsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loaderVersionsCts = cts;
        LoaderVersionHint.Text = $"正在查询 Minecraft {version.Id} 可用的 {loader} 版本…";
        try
        {
            var list = loader switch
            {
                LoaderKind.Fabric => await _services.LoaderInstaller.GetFabricVersionsAsync(version.Id, cts.Token),
                LoaderKind.Quilt => await _services.LoaderInstaller.GetQuiltVersionsAsync(version.Id, cts.Token),
                LoaderKind.Forge => await _services.LoaderInstaller.GetForgeVersionsAsync(version.Id, cts.Token),
                LoaderKind.NeoForge => await _services.LoaderInstaller.GetNeoForgeVersionsAsync(version.Id, cts.Token),
                _ => Array.Empty<LoaderVersionInfo>()
            };
            if (cts.IsCancellationRequested || !ReferenceEquals(_loaderVersionsCts, cts)) return;

            LoaderVersionBox.Items.Clear();
            foreach (var item in list)
            {
                LoaderVersionBox.Items.Add(item);
            }

            var recommended = list.FirstOrDefault(v => v.Recommended) ?? list.FirstOrDefault();
            LoaderVersionBox.SelectedItem = recommended;
            LoaderVersionBox.Text = recommended?.Version ?? "";
            LoaderVersionHint.Text = list.Count == 0
                ? $"没有找到适用于 Minecraft {version.Id} 的 {loader} 版本。"
                : $"Minecraft {version.Id} 推荐 {loader} {recommended!.Version}；也可以选择其他版本或直接输入。";
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                LoaderVersionHint.Text = $"加载器版本查询失败：{ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_loaderVersionsCts, cts))
            {
                _loaderVersionsCts = null;
            }

            cts.Dispose();
        }
    }

    private void AddMods_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要加入游戏的模组",
            Filter = "模组文件 (*.jar)|*.jar|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            if (!_selectedMods.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _selectedMods.Add(file);
            }
        }

        UpdateModsSummary();
    }

    private void AddModrinthMods_Click(object sender, RoutedEventArgs e)
    {
        var version = VersionBox.SelectedItem as MinecraftVersionInfo;
        if (version is null)
        {
            ShowError("请先选择一个 Minecraft 版本");
            return;
        }

        if (SelectedLoader == LoaderKind.Vanilla)
        {
            ShowError("原版游戏不能加载模组，请先选择 Fabric、Quilt、Forge 或 NeoForge");
            return;
        }

        var picker = new ModrinthPickerWindow(_services, version.Id, SelectedLoader) { Owner = this };
        if (picker.ShowDialog() != true) return;
        foreach (var project in picker.SelectedProjects)
        {
            if (_selectedProjects.All(p => p.ProjectId != project.ProjectId))
            {
                _selectedProjects.Add(project);
            }
        }

        UpdateModsSummary();
    }

    private void UpdateModsSummary()
    {
        if (ModsSummary is null) return;
        var parts = new List<string>();
        if (_selectedMods.Count > 0) parts.Add($"本地 {_selectedMods.Count}");
        if (_selectedProjects.Count > 0) parts.Add($"在线 {_selectedProjects.Count}");
        ModsSummary.Text = parts.Count == 0 ? "未选择" : string.Join(" · ", parts);
    }

    private LoaderKind SelectedLoader => LoaderFabric.IsChecked == true
        ? LoaderKind.Fabric
        : LoaderQuilt.IsChecked == true
            ? LoaderKind.Quilt
            : LoaderForge.IsChecked == true
                ? LoaderKind.Forge
                : LoaderNeoForge.IsChecked == true
                    ? LoaderKind.NeoForge
                    : LoaderKind.Vanilla;

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _installing)
        {
            return;
        }

        var version = VersionBox.SelectedItem as MinecraftVersionInfo;
        if (version is null)
        {
            ShowError("请选择一个 Minecraft 版本");
            return;
        }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("游戏名称不能为空");
            return;
        }

        var loaderVersion = LoaderVersionBox.Text.Trim();
        if (loaderVersion.Length > 0 && loaderVersion.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
        {
            ShowError("加载器版本只能包含字母、数字、点、横线、下划线或加号");
            return;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        _installing = true;
        _closeRequested = false;
        _cts = new CancellationTokenSource();
        SetFormEnabled(false);
        InstallButton.IsEnabled = false;
        CancelButton.Content = "取消安装";
        ProgressPanel.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(new Action(() => FormScroll.ScrollToEnd()));
        ProgressStage.Text = "准备安装";
        ProgressDetail.Text = "正在建立游戏目录…";
        ProgressCount.Text = "";
        InstallProgressBar.Value = 0;
        ProgressPercent.Text = "0%";

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgressBar.Value = Math.Clamp(p.Fraction, 0, 1);
            ProgressPercent.Text = $"{p.Fraction:P0}";
            ProgressStage.Text = p.Stage;
            ProgressDetail.Text = p.Detail;
            ProgressCount.Text = p.FilesTotal > 0 ? $"{p.FilesDone} / {p.FilesTotal} 个文件" : "";
        });

        try
        {
            var selectedLoader = SelectedLoader;
            Result = selectedLoader is LoaderKind.Forge or LoaderKind.NeoForge
                ? await _services.LoaderInstaller.InstallAsync(selectedLoader, version, name,
                    string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion, progress, _cts.Token)
                : await _services.Installer.InstallAsync(new InstallRequest(version, name, selectedLoader,
                    string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion), progress, _cts.Token);
            var copiedMods = 0;
            if (selectedLoader != LoaderKind.Vanilla && _selectedMods.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(Result.ModsDirectory);
                    foreach (var file in _selectedMods)
                    {
                        var modName = PathSafety.SafeFileName(Path.GetFileName(file), "mod.jar");
                        File.Copy(file, Path.Combine(Result.ModsDirectory, modName), true);
                        copiedMods++;
                    }
                }
                catch (Exception ex)
                {
                    _services.Log.Error("复制创建时选择的模组失败", ex);
                    _state.Notify("游戏已创建，但部分模组复制失败：" + ex.Message, true);
                }
            }

            var onlineInstalled = 0;
            var failedOnline = new List<string>();
            if (selectedLoader != LoaderKind.Vanilla && _selectedProjects.Count > 0)
            {
                ProgressStage.Text = "安装模组";
                var status = new Progress<string>(text => ProgressDetail.Text = text);
                foreach (var project in _selectedProjects)
                {
                    try
                    {
                        ProgressDetail.Text = $"正在安装 {project.Title}…";
                        await _services.Modrinth.InstallAsync(project, Result, "mod", status);
                        onlineInstalled++;
                    }
                    catch (Exception ex)
                    {
                        _services.Log.Error($"安装创建时选择的模组失败：{project.Title}", ex);
                        failedOnline.Add(project.Title);
                    }
                }
            }

            var totalMods = copiedMods + onlineInstalled;
            var message = totalMods > 0
                ? $"游戏「{Result.Name}」安装完成，已添加 {totalMods} 个模组"
                : $"游戏「{Result.Name}」安装完成";
            if (failedOnline.Count > 0)
            {
                message += $"；{failedOnline.Count} 个模组安装失败，可在模组页重试";
            }

            _state.Notify(message, failedOnline.Count > 0);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            ProgressStage.Text = "已取消";
            ProgressDetail.Text = "安装没有完成，可以重新开始。";
            ShowError("安装已取消。已下载的文件会保留，下次安装可以继续复用。");
        }
        catch (Exception ex)
        {
            _services.Log.Error("安装游戏失败", ex);
            ProgressStage.Text = "安装失败";
            ProgressDetail.Text = ex.Message;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ShowError("安装失败：" + ex.Message);
        }
        finally
        {
            _installing = false;
            _cts?.Dispose();
            _cts = null;
            SetFormEnabled(true);
            InstallButton.IsEnabled = true;
            CancelButton.Content = "取消";
            if (_closeRequested)
            {
                Close();
            }
        }
    }

    private void SetFormEnabled(bool enabled)
    {
        FormPanel.IsHitTestVisible = enabled;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            _closeRequested = true;
            _cts?.Cancel();
            ProgressStage.Text = "正在取消…";
            ProgressDetail.Text = "等待安装任务停止…";
            return;
        }

        DialogResult = false;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            _closeRequested = true;
            _cts?.Cancel();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
