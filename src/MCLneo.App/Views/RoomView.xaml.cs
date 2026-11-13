using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCLneo.App.Services;

namespace MCLneo.App.Views;

public partial class RoomView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;
    private readonly DispatcherTimer _playersTimer;

    public RoomView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        AdminWarning.Visibility = ElevationService.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        _playersTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _playersTimer.Tick += async (_, _) => await _state.RefreshRoomPlayersAsync();
        Loaded += async (_, _) =>
        {
            _playersTimer.Start();
            _state.RefreshIpv6Status();
            await _state.RefreshRoomPlayersAsync();
        };
        Unloaded += (_, _) => _playersTimer.Stop();
    }

    private async void RefreshPlayers_Click(object sender, RoutedEventArgs e)
    {
        await _state.RefreshRoomPlayersAsync();
    }

    private async void CheckIpv6_Click(object sender, RoutedEventArgs e)
    {
        int? port = int.TryParse(HostPortBox.Text.Trim(), out var parsed) &&
                    parsed is >= 1024 and <= 65535
            ? parsed
            : null;
        await _state.CheckIpv6Async(port);
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HostPortBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            _state.Notify("请输入 1024–65535 的有效端口", true);
            HostPortBox.Focus();
            return;
        }

        var mode = ConnectionModeBox.SelectedIndex;
        _state.RefreshIpv6Status();
        var useDirect = mode == 1 || (mode == 0 && _state.Ipv6Available);
        if (useDirect)
        {
            await _state.HostDirectAsync(port);
            await _state.RefreshRoomPlayersAsync();
            if (mode == 0 && !_state.IsDirectActive)
            {
                // Automatic mode: fall back to EasyTier when the IPv6 path
                // cannot start (for example, no inbound IPv6 route).
                if (!EnsureElevated()) return;
                await _state.HostRoomAsync(port);
                await _state.RefreshRoomPlayersAsync();
            }
        }
        else
        {
            if (!EnsureElevated()) return;
            await _state.HostRoomAsync(port);
            await _state.RefreshRoomPlayersAsync();
        }

        if (_state.IsRoomActive && !string.IsNullOrWhiteSpace(_state.RoomInviteCode))
        {
            TryCopy(_state.RoomInviteCode, "邀请码已复制到剪贴板");
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var value = JoinInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _state.Notify("请输入 IPv6 地址或邀请码", true);
            JoinInputBox.Focus();
            return;
        }

        var mode = ConnectionModeBox.SelectedIndex;
        var looksDirect = value.StartsWith("[", StringComparison.Ordinal) &&
                          value.Contains("]:", StringComparison.Ordinal);
        var looksInvite = value.StartsWith("MCLN1:", StringComparison.OrdinalIgnoreCase);
        var useDirect = mode == 1 || (mode == 0 && looksDirect);
        if (useDirect)
        {
            await _state.JoinDirectAsync(value);
            await _state.RefreshRoomPlayersAsync();
            return;
        }

        if (mode == 1)
        {
            _state.Notify("IPv6 直连地址格式应为 [IPv6]:端口", true);
            return;
        }

        if (!looksInvite && mode != 2)
        {
            _state.Notify("无法识别地址。IPv6 直连请填 [IPv6]:端口，EasyTier 请粘贴 MCLN1: 邀请码", true);
            return;
        }

        if (!EnsureElevated()) return;
        await _state.JoinRoomAsync(value);
        await _state.RefreshRoomPlayersAsync();
        if (_state.IsRoomActive && !string.IsNullOrWhiteSpace(_state.RoomConnectAddress))
        {
            TryCopy(_state.RoomConnectAddress, "连接地址已复制到剪贴板");
        }
    }

    private void CopyDirect_Click(object sender, RoutedEventArgs e)
    {
        var text = _state.DirectShareAddress ?? _state.DirectConnectAddress;
        if (!string.IsNullOrWhiteSpace(text))
        {
            TryCopy(text, "IPv6 直连地址已复制");
        }
    }

    private async void StopDirect_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(Window.GetWindow(this), "断开 IPv6 直连",
                "断开后朋友将无法继续连接。确定要断开吗？", "断开", danger: true))
        {
            return;
        }

        await _state.StopDirectAsync();
    }

    private bool EnsureElevated()
    {
        if (ElevationService.IsElevated)
        {
            return true;
        }

        var restart = MessageDialog.Confirm(Window.GetWindow(this), "需要管理员权限",
            "EasyTier 需要创建虚拟网卡才能建立联机房间。\n是否以管理员身份重新启动 MCLneo？\n\n（正在运行的游戏或下载会中断，请先保存。）",
            "以管理员身份重启");
        if (!restart)
        {
            _state.Notify("联机房间需要管理员权限", true);
            return false;
        }

        ElevationService.TryRestartElevated("room", out var error);
        if (!string.IsNullOrWhiteSpace(error))
        {
            _state.Notify("重启失败：" + error, true);
        }

        return false;
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) => EnsureElevated();

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_state.RoomInviteCode))
        {
            TryCopy(_state.RoomInviteCode, "邀请码已复制");
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_state.RoomConnectAddress))
        {
            TryCopy(_state.RoomConnectAddress, "连接地址已复制");
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(Window.GetWindow(this), "离开房间",
                "离开后联机模块会关闭，游戏本身不会退出。确定要离开吗？", "离开房间", danger: true))
        {
            return;
        }

        await _state.StopRoomAsync();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Paths.LogsDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _state.Notify("无法打开日志目录：" + ex.Message, true);
        }
    }

    private void TryCopy(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            _state.Notify(successMessage, false);
        }
        catch (Exception ex)
        {
            _state.Notify("复制失败：" + ex.Message, true);
        }
    }
}
