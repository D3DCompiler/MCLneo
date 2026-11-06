using System.Windows;
using System.Windows.Controls;
using MCLneo.App.Services;
using MCLneo.Core;
using MCLneo.Core.Models;

namespace MCLneo.App.Views;

public partial class DownloadView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private List<MinecraftVersionInfo> _all = new();
    private bool _loading;
    private bool _initialized;

    public DownloadView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        SourceBox.Items.Add("BMCLAPI 镜像");
        SourceBox.Items.Add("Mojang 官方");
        SourceBox.SelectedIndex = services.Settings.Current.DownloadSource == DownloadSource.Bmclapi ? 0 : 1;
        _initialized = true;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0)
        {
            await LoadAsync(force: false);
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        VersionList.Visibility = Visibility.Collapsed;
        try
        {
            var manifest = await _services.Catalog.GetManifestAsync(_services.Settings.Current.DownloadSource, force);
            _all = manifest.Versions.ToList();
            ApplyFilter();
            VersionList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取版本清单失败", ex);
            ErrorText.Text = ex.Message;
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var includeSnapshots = SnapshotCheck.IsChecked == true;
        var visible = _all
            .Where(v => includeSnapshots || v.Type == "release")
            .Where(v => string.IsNullOrWhiteSpace(query) ||
                        v.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.ReleaseTime)
            .ToList();
        VersionList.ItemsSource = visible;
        CountText.Text = $"{visible.Count} 个版本";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized && _all.Count > 0) ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized && _all.Count > 0) ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(force: true);
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        var source = SourceBox.SelectedIndex == 0 ? DownloadSource.Bmclapi : DownloadSource.Official;
        if (_services.Settings.Current.DownloadSource == source) return;
        _services.Settings.Current.DownloadSource = source;
        await _services.Settings.SaveAsync();
        _all.Clear();
        await LoadAsync(force: true);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MinecraftVersionInfo version })
        {
            return;
        }

        var dialog = new CreateInstanceDialog(_services, _state, version.Id) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await _state.RefreshInstancesAsync(dialog.Result.Id);
            _state.SelectedInstance = _state.Instances.FirstOrDefault(i => i.Id == dialog.Result.Id) ?? dialog.Result;
        }
    }
}
