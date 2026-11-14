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
    private bool _pinging;
    private CancellationTokenSource? _pingCts;

    public ServersView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
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
        _state.Instances.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => RefreshServerUi(load: true)));
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Instances))
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshServerUi(load: true)));
        }
    }

    private void RefreshServerUi(bool load)
    {
        if (_state.Instances.Count == 0)
        {
            NoInstancePanel.Visibility = Visibility.Visible;
            ServerList.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NoInstancePanel.Visibility = Visibility.Collapsed;
        ServerList.Visibility = Visibility.Visible;
        if (load) LoadServers();
    }

    private void LoadServers()
    {
        try
        {
            _rows.Clear();
            var instances = _state.Instances.ToList();
            foreach (var instance in instances)
            {
                foreach (var entry in ServerListService.Read(instance))
                {
                    _rows.Add(new ServerRow(entry, instance, instances));
                }
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
        LoadServers();
    }

    private void VersionBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: ServerRow row } combo) return;
        combo.ItemsSource = row.Instances;
        var selected = row.Instances.FirstOrDefault(i => i.Id == row.SelectedInstance?.Id)
                       ?? row.Instances.FirstOrDefault(i => i.Id == row.Owner.Id)
                       ?? row.Instances.FirstOrDefault();
        combo.SelectedItem = selected;
        row.SelectedInstance = selected;
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRow row }) return;
        row.BeginQuery();
        var status = await ServerStatusService.QueryAsync(
            row.Entry.Address, row.Entry.Port ?? 25565, 4500);
        row.Apply(status);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var instance = _state.SelectedInstance ?? _state.Instances.FirstOrDefault();
        if (instance is null)
        {
            _state.Notify("请先导入一个游戏", true);
            return;
        }

        var name = InputDialog.Show(Window.GetWindow(this), "添加服务器", "服务器显示名称", "Minecraft 服务器");
        if (string.IsNullOrWhiteSpace(name)) return;
        var address = InputDialog.Show(Window.GetWindow(this), "添加服务器", "服务器地址，例如 play.example.com:25565",
            "localhost:25565", ValidateAddress);
        if (string.IsNullOrWhiteSpace(address)) return;
        var entries = ServerListService.Read(instance).ToList();
        entries.Add(new ServerEntry(name.Trim(), address.Trim()));
        Save(instance, entries);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRow row }) return;
        var instance = row.Owner;
        var entries = ServerListService.Read(instance).ToList();
        var entry = row.Entry;
        var index = entries.IndexOf(entry);
        if (index < 0) return;
        var name = InputDialog.Show(Window.GetWindow(this), "编辑服务器", "服务器显示名称", entry.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var address = InputDialog.Show(Window.GetWindow(this), "编辑服务器", "服务器地址", entry.Ip, ValidateAddress);
        if (string.IsNullOrWhiteSpace(address)) return;
        entries[index] = entry with { Name = name.Trim(), Ip = address.Trim() };
        Save(instance, entries);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRow row }) return;
        var instance = row.Owner;
        var entry = row.Entry;
        if (!MessageDialog.Confirm(Window.GetWindow(this), "删除服务器", $"确定删除「{entry.Name}」吗？", "删除",
                danger: true))
        {
            return;
        }

        var entries = ServerListService.Read(instance).ToList();
        entries.RemoveAll(e => string.Equals(e.Name, entry.Name, StringComparison.Ordinal) &&
                               string.Equals(e.Ip, entry.Ip, StringComparison.OrdinalIgnoreCase));
        Save(instance, entries);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRow row }) return;
        var instance = row.SelectedInstance ?? row.Owner;
        _state.SelectedInstance = instance;
        await _state.LaunchAsync(instance, row.Entry.Address, row.Entry.Port);
    }

    private void Save(GameInstance instance, IReadOnlyList<ServerEntry> entries)
    {
        try
        {
            ServerListService.Write(instance, entries);
            LoadServers();
            _state.Notify($"服务器列表已保存到「{instance.Name}」", false);
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
