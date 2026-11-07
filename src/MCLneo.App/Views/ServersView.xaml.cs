using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCLneo.App.Services;
using MCLneo.Core.Models;
using MCLneo.Core.Servers;

namespace MCLneo.App.Views;

public partial class ServersView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly ObservableCollection<ServerRow> _rows = new();
    private readonly DispatcherTimer _autoRefresh;
    private bool _initialized;
    private bool _pinging;
    private CancellationTokenSource? _pingCts;
    private List<ServerEntry> _entries = new();

    public ServersView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        _initialized = true;
        ServerList.ItemsSource = _rows;
        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRefresh.Tick += (_, _) => _ = PingRowsAsync();
        Loaded += (_, _) =>
        {
            RefreshServerUi(load: true);
            _autoRefresh.Start();
        };
        Unloaded += (_, _) =>
        {
            _autoRefresh.Stop();
            CancelPings();
        };
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
            _rows.Clear();
            foreach (var entry in _entries)
            {
                _rows.Add(new ServerRow(entry));
            }

            EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _ = PingRowsAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Error("读取服务器列表失败", ex);
            _state.Notify("读取服务器列表失败：" + ex.Message, true);
        }
    }

    private async Task PingRowsAsync()
    {
        if (_pinging || _rows.Count == 0) return;
        _pinging = true;
        CancelPings();
        var cts = new CancellationTokenSource();
        _pingCts = cts;
        try
        {
            await Task.WhenAll(_rows.Select(async row =>
            {
                var status = await ServerStatusService.QueryAsync(
                    row.Entry.Address, row.Entry.Port ?? 25565, 4500, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() => row.Apply(status));
                }
            }));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Warn("查询服务器状态失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_pingCts, cts)) _pingCts = null;
            cts.Dispose();
            _pinging = false;
        }
    }

    private void CancelPings()
    {
        try { _pingCts?.Cancel(); } catch { }
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
        if (instance is null || sender is not FrameworkElement { Tag: ServerRow row }) return;
        var entry = row.Entry;
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
        if (instance is null || sender is not FrameworkElement { Tag: ServerRow row }) return;
        var entry = row.Entry;
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
        if (instance is null || sender is not FrameworkElement { Tag: ServerRow row }) return;
        _state.SelectedInstance = instance;
        await _state.LaunchAsync(instance, row.Entry.Address, row.Entry.Port);
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
