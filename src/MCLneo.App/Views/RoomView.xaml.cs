using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MCLneo.App.Services;

namespace MCLneo.App.Views;

public partial class RoomView : UserControl
{
    private readonly AppServices _services;
    private readonly AppState _state;

    public RoomView(AppServices services, AppState state)
    {
        InitializeComponent();
        _services = services;
        _state = state;
        DataContext = state;
        AdminWarning.Visibility = ElevationService.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HostPortBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            _state.Notify("请输入 1024–65535 的有效端口", true);
            HostPortBox.Focus();
            return;
        }

        if (!EnsureElevated())
        {
            return;
        }

        await _state.HostRoomAsync(port);
        if (_state.IsRoomActive && !string.IsNullOrWhiteSpace(_state.RoomInviteCode))
        {
            TryCopy(_state.RoomInviteCode, "邀请码已复制到剪贴板");
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var code = JoinCodeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            _state.Notify("请输入邀请码", true);
            return;
        }

        if (!EnsureElevated())
        {
            return;
        }

        await _state.JoinRoomAsync(code);
        if (_state.IsRoomActive && !string.IsNullOrWhiteSpace(_state.RoomConnectAddress))
        {
            TryCopy(_state.RoomConnectAddress, "连接地址已复制到剪贴板");
        }
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

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                JoinCodeBox.Text = Clipboard.GetText().Trim();
            }
        }
        catch (Exception ex)
        {
            _state.Notify("无法读取剪贴板：" + ex.Message, true);
        }
    }

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
