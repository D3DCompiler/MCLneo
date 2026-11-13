using System.ComponentModel;
using System.Runtime.CompilerServices;
using MCLneo.Core.Servers;

namespace MCLneo.App.Views;

/// <summary>View model for one row in the server list, including live status.</summary>
public sealed class ServerRow : INotifyPropertyChanged
{
    private ServerStatus? _status;
    private IReadOnlyList<string> _players = Array.Empty<string>();

    public ServerRow(ServerEntry entry)
    {
        Entry = entry;
    }

    public ServerEntry Entry { get; }
    public string Name => Entry.Name;
    public string Ip => Entry.Ip;
    public IReadOnlyList<string> Players => _players;
    public bool HasPlayers => _players.Count > 0;
    public bool ShowEmptyPlayers => _status is { Online: true } && _players.Count == 0;
    public string OnlineText => _status is null
        ? "正在查询状态…"
        : _status.Online
            ? $"{_status.OnlinePlayers}/{_status.MaxPlayers} 人在线"
            : "离线或无法连接";
    public string DetailText => _status is { Online: true }
        ? _status.Error ?? (_status.Version.Length > 0 ? "Minecraft " + _status.Version : "")
        : _status?.Error ?? "";

    public void BeginQuery()
    {
        _status = null;
        _players = Array.Empty<string>();
        OnPropertyChanged(nameof(Players));
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(ShowEmptyPlayers));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(DetailText));
    }

    public void Apply(ServerStatus status)
    {
        _status = status;
        _players = status.Players;
        OnPropertyChanged(nameof(Players));
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(ShowEmptyPlayers));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(DetailText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
