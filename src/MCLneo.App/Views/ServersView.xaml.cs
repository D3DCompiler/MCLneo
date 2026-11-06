using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MCLneo.App.Services;
using MCLneo.Core.Models;
using MCLneo.Core.Servers;

namespace MCLneo.App.Views;

public partial class ServersView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private bool _initialized;
    private List<ServerEntry> _entries = new();

    public ServersView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        _initialized = true;
        Loaded += (_, _) => RefreshServerUi(load: true);
        _state.PropertyChanged += State_PropertyChanged;
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.SelectedInstance))
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshServerUi(load: true)));
        }
    }

    private void InstanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) RefreshServerUi(load: true);
    }

    private void RefreshServerUi(bool load)
    {
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            NoInstancePanel.Visibility = Visibility.Visible;
            ServerList.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            HintText.Text = "未选择游戏";
            return;
        }

        NoInstancePanel.Visibility = Visibility.Collapsed;
        ServerList.Visibility = Visibility.Visible;
        HintText.Text = $"servers.dat：{ServerListService.GetPath(instance)}";
        if (load) LoadServers(instance);
    }

    private void LoadServers(GameInstance instance)
    {
        try
        {
            _entries = ServerListService.Read(instance).ToList();
            ServerList.ItemsSource = null;
            ServerList.ItemsSource = _entries;
            EmptyPanel.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取服务器列表失败", ex);
            _state.Notify("读取服务器列表失败：" + ex.Message, true);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is not null) LoadServers(instance);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is null)
        {
            _state.Notify("请先选择一个游戏", true);
            return;
        }

        var name = InputDialog.Show(Window.GetWindow(this), "添加服务器", "服务器显示名称", "Minecraft 服务器");
        if (string.IsNullOrWhiteSpace(name)) return;
        var address = InputDialog.Show(Window.GetWindow(this), "添加服务器", "服务器地址，例如 play.example.com:25565",
            "localhost:25565", ValidateAddress);
        if (string.IsNullOrWhiteSpace(address)) return;
        _entries.Add(new ServerEntry(name.Trim(), address.Trim()));
        Save(instance);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is null || sender is not FrameworkElement { Tag: ServerEntry entry }) return;
        var index = _entries.IndexOf(entry);
        if (index < 0) return;
        var name = InputDialog.Show(Window.GetWindow(this), "编辑服务器", "服务器显示名称", entry.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var address = InputDialog.Show(Window.GetWindow(this), "编辑服务器", "服务器地址", entry.Ip, ValidateAddress);
        if (string.IsNullOrWhiteSpace(address)) return;
        _entries[index] = entry with { Name = name.Trim(), Ip = address.Trim() };
        Save(instance);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is null || sender is not FrameworkElement { Tag: ServerEntry entry }) return;
        if (!MessageDialog.Confirm(Window.GetWindow(this), "删除服务器", $"确定删除「{entry.Name}」吗？", "删除",
                danger: true))
        {
            return;
        }

        _entries.Remove(entry);
        Save(instance);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance;
        if (instance is null || sender is not FrameworkElement { Tag: ServerEntry entry }) return;
        _state.SelectedInstance = instance;
        await _state.LaunchAsync(instance, entry.Address, entry.Port);
    }

    private void Save(GameInstance instance)
    {
        try
        {
            ServerListService.Write(instance, _entries);
            LoadServers(instance);
            _state.Notify("服务器列表已保存", false);
        }
        catch (Exception ex)
        {
            _state.Notify("保存服务器列表失败：" + ex.Message, true);
        }
    }

    private static string? ValidateAddress(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text)) return "服务器地址不能为空";
        if (text.Any(char.IsWhiteSpace)) return "服务器地址不能包含空格";
        if (text.StartsWith(':')) return "服务器地址无效";
        return null;
    }
}
