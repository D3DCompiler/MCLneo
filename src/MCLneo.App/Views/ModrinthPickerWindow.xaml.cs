using System.Windows;
using System.Windows.Input;
using MCLneo.App.Services;
using MCLneo.Core;
using MCLneo.Core.Mods;

namespace MCLneo.App.Views;

public partial class ModrinthPickerWindow : Window
{
    private readonly AppServices _services;
    private readonly string _minecraftVersion;
    private readonly LoaderKind _loader;
    private bool _loading;

    public ModrinthPickerWindow(AppServices services, string minecraftVersion, LoaderKind loader)
    {
        InitializeComponent();
        _services = services;
        _minecraftVersion = minecraftVersion;
        _loader = loader;
        FilterText.Text = $"Minecraft {minecraftVersion} · {loader.DisplayName()} · 点击“添加”加入当前游戏";
        SearchBox.Focus();
    }

    public List<ModrinthProject> SelectedProjects { get; } = new();

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        if (_loading) return;
        _loading = true;
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var loader = ModrinthService.LoaderId(_loader);
            var response = await _services.Modrinth.SearchAsync(
                SearchBox.Text.Trim(), "mod", _minecraftVersion, loader, 0, 20,
                string.IsNullOrWhiteSpace(SearchBox.Text) ? "downloads" : "relevance");
            ResultsList.ItemsSource = response.Hits;
            EmptyText.Visibility = response.Hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = response.Hits.Count == 0 ? "没有找到兼容当前版本和加载器的模组" : "";
        }
        catch (Exception ex)
        {
            _services.Log.Error("搜索创建游戏模组失败", ex);
            EmptyText.Text = "搜索失败：" + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModrinthProject project }) return;
        if (SelectedProjects.All(p => p.ProjectId != project.ProjectId))
        {
            SelectedProjects.Add(project);
        }

        StatusText.Text = $"已选择 {SelectedProjects.Count} 个模组，创建完成后自动安装";
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

internal static class LoaderKindDisplay
{
    public static string DisplayName(this LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版"
    };
}
