using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MCLneo.Core;
using MCLneo.Core.Accounts;
using MCLneo.Core.Minecraft;
using MCLneo.Core.Models;
using MCLneo.Core.Rooms;

namespace MCLneo.App.Services;

public sealed class AppState : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private GameInstance? _selectedInstance;
    private string _playerName = "Steve";
    private AuthlibAccount? _activeAuthlib;
    private AuthlibAccountStore.AccountFile _accountFile = new();
    private bool _isLaunching;
    private string _launchTitle = "正在启动游戏";
    private string _launchDetail = "准备中…";
    private double _launchFraction;
    private string? _launchError;
    private bool _showLaunchOverlay;
    private GameSession? _activeSession;
    private bool _isRoomBusy;
    private string _roomStatus = "";
    private double _roomProgress;
    private RoomSession? _activeRoom;
    private string? _roomInviteCode;
    private string? _roomConnectAddress;
    private Ipv6DirectSession? _directSession;
    private bool _isDirectBusy;
    private string _directStatus = "";
    private double _directProgress;
    private string? _directShareAddress;
    private string? _directConnectAddress;
    private string _directRoleText = "";
    private string _roomRoleText = "未连接";
    private bool _isInitialized;

    public AppState(AppServices services)
    {
        _services = services;
        Instances = new ObservableCollection<GameInstance>();
        JavaRuntimes = new ObservableCollection<JavaRuntime>();
        PlayerName = services.Settings.Current.LastPlayerName;
    }

    public AppServices Services => _services;
    public LauncherSettings Settings => _services.Settings.Current;
    public ObservableCollection<GameInstance> Instances { get; }
    public ObservableCollection<JavaRuntime> JavaRuntimes { get; }

    public event Action<string, bool>? ToastRequested;

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
            {
                return;
            }

            Settings.SelectedInstance = value?.Id;
            OnPropertyChanged(nameof(HasSelectedInstance));
            OnPropertyChanged(nameof(SelectedInstanceTitle));
            OnPropertyChanged(nameof(SelectedInstanceSubtitle));
            _ = _services.Settings.SaveAsync();
        }
    }

    public bool HasSelectedInstance => SelectedInstance is not null;
    public string SelectedInstanceTitle => SelectedInstance?.Name ?? "还没有游戏";
    public string SelectedInstanceSubtitle => SelectedInstance?.Subtitle ?? "先创建一个 Minecraft 游戏，开始你的冒险。";

    public string PlayerName
    {
        get => _playerName;
        set
        {
            if (!SetProperty(ref _playerName, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PlayerInitial));
            OnPropertyChanged(nameof(Greeting));
            OnPropertyChanged(nameof(Player));
        }
    }

    public string PlayerInitial => string.IsNullOrWhiteSpace(PlayerName) ? "S" : PlayerName[..1].ToUpperInvariant();

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            var part = hour switch
            {
                < 6 => "夜深了",
                < 11 => "早上好",
                < 14 => "中午好",
                < 18 => "下午好",
                _ => "晚上好"
            };
            return $"{part}，{PlayerName}";
        }
    }

    public string TodayText => DateTime.Now.ToString("M月d日 · dddd");

    public AuthlibAccount? ActiveAuthlibAccount => _activeAuthlib;

    public bool IsAuthlibActive => _activeAuthlib is not null;

    public PlayerProfile Player
    {
        get
        {
            if (_activeAuthlib is not null)
            {
                return AuthlibAccountStore.ToPlayer(_activeAuthlib);
            }

            try
            {
                return PlayerProfile.Offline(PlayerName);
            }
            catch
            {
                return PlayerProfile.Offline("Steve");
            }
        }
    }

    public string AccountSubtitle => _activeAuthlib is null
        ? "离线账户"
        : $"第三方 · {_activeAuthlib.ServerDisplay}";

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (SetProperty(ref _isLaunching, value))
            {
                OnPropertyChanged(nameof(LaunchButtonText));
            }
        }
    }

    public string LaunchButtonText => IsLaunching ? "正在启动…" : "启动游戏";

    public string LaunchTitle
    {
        get => _launchTitle;
        private set => SetProperty(ref _launchTitle, value);
    }

    public string LaunchDetail
    {
        get => _launchDetail;
        private set => SetProperty(ref _launchDetail, value);
    }

    public double LaunchFraction
    {
        get => _launchFraction;
        private set => SetProperty(ref _launchFraction, value);
    }

    public string? LaunchError
    {
        get => _launchError;
        private set
        {
            if (SetProperty(ref _launchError, value))
            {
                OnPropertyChanged(nameof(HasLaunchError));
            }
        }
    }

    public bool HasLaunchError => !string.IsNullOrWhiteSpace(LaunchError);

    public bool ShowLaunchOverlay
    {
        get => _showLaunchOverlay;
        private set => SetProperty(ref _showLaunchOverlay, value);
    }

    public GameSession? ActiveSession
    {
        get => _activeSession;
        private set
        {
            if (SetProperty(ref _activeSession, value))
            {
                OnPropertyChanged(nameof(IsGameRunning));
                OnPropertyChanged(nameof(HasActiveGame));
            }
        }
    }

    public bool IsGameRunning => ActiveSession?.IsRunning == true;
    public bool HasActiveGame => ActiveSession is not null;

    public bool IsRoomBusy
    {
        get => _isRoomBusy;
        private set => SetProperty(ref _isRoomBusy, value);
    }

    public string RoomStatus
    {
        get => _roomStatus;
        private set => SetProperty(ref _roomStatus, value);
    }

    public double RoomProgress
    {
        get => _roomProgress;
        private set => SetProperty(ref _roomProgress, value);
    }

    public RoomSession? ActiveRoom
    {
        get => _activeRoom;
        private set
        {
            if (SetProperty(ref _activeRoom, value))
            {
                OnPropertyChanged(nameof(IsRoomActive));
                OnPropertyChanged(nameof(RoomConnectAddress));
            }
        }
    }

    public bool IsRoomActive => ActiveRoom?.IsRunning == true;

    public string? RoomInviteCode
    {
        get => _roomInviteCode;
        private set => SetProperty(ref _roomInviteCode, value);
    }

    public string? RoomConnectAddress
    {
        get => _roomConnectAddress;
        private set => SetProperty(ref _roomConnectAddress, value);
    }

    public bool IsDirectActive => _directSession?.IsRunning == true;

    public bool IsDirectBusy
    {
        get => _isDirectBusy;
        private set => SetProperty(ref _isDirectBusy, value);
    }

    public string DirectStatus
    {
        get => _directStatus;
        private set => SetProperty(ref _directStatus, value);
    }

    public double DirectProgress
    {
        get => _directProgress;
        private set => SetProperty(ref _directProgress, value);
    }

    public string? DirectShareAddress
    {
        get => _directShareAddress;
        private set => SetProperty(ref _directShareAddress, value);
    }

    public string? DirectConnectAddress
    {
        get => _directConnectAddress;
        private set => SetProperty(ref _directConnectAddress, value);
    }

    public string DirectRoleText
    {
        get => _directRoleText;
        private set => SetProperty(ref _directRoleText, value);
    }

    public string RoomRoleText
    {
        get => _roomRoleText;
        private set => SetProperty(ref _roomRoleText, value);
    }

    public async Task InitializeAsync()
    {
        _accountFile = await _services.AuthlibStore.LoadAsync();
        _activeAuthlib = _accountFile.Accounts.FirstOrDefault(a =>
            string.Equals(a.Key, _accountFile.ActiveKey, StringComparison.OrdinalIgnoreCase));
        if (_activeAuthlib is not null)
        {
            PlayerName = _activeAuthlib.PlayerName;
        }

        await ReloadJavaAsync();
        await RefreshInstancesAsync();
        IsInitialized = true;
    }

    public async Task LoginAuthlibAsync(string server, string username, string password)
    {
        var account = await _services.Authlib.LoginAsync(server, username, password,
            new Progress<string>(_ => { }));
        var existing = _accountFile.Accounts.FindIndex(a =>
            string.Equals(a.Key, account.Key, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _accountFile.Accounts[existing] = account;
        }
        else
        {
            _accountFile.Accounts.Add(account);
        }

        _accountFile.ActiveKey = account.Key;
        await _services.AuthlibStore.SaveAsync(_accountFile);
        _activeAuthlib = account;
        PlayerName = account.PlayerName;
        OnPropertyChanged(nameof(ActiveAuthlibAccount));
        OnPropertyChanged(nameof(IsAuthlibActive));
        OnPropertyChanged(nameof(AccountSubtitle));
        Notify($"已登录第三方账户 {account.PlayerName}", false);
    }

    public async Task LogoutAuthlibAsync()
    {
        _activeAuthlib = null;
        _accountFile.ActiveKey = null;
        await _services.AuthlibStore.SaveAsync(_accountFile);
        PlayerName = Settings.LastPlayerName;
        OnPropertyChanged(nameof(ActiveAuthlibAccount));
        OnPropertyChanged(nameof(IsAuthlibActive));
        OnPropertyChanged(nameof(AccountSubtitle));
        Notify("已切换到离线账户", false);
    }

    public async Task RefreshInstancesAsync(string? selectId = null)
    {
        var previous = selectId ?? SelectedInstance?.Id;
        var items = await _services.Instances.ScanAsync();
        Instances.Clear();
        foreach (var item in items)
        {
            Instances.Add(item);
        }

        var target = items.FirstOrDefault(i => string.Equals(i.Id, previous, StringComparison.OrdinalIgnoreCase))
                     ?? items.FirstOrDefault(i => string.Equals(i.Id, Settings.SelectedInstance,
                         StringComparison.OrdinalIgnoreCase))
                     ?? items.FirstOrDefault();
        SelectedInstance = target;
        OnPropertyChanged(nameof(HasSelectedInstance));
    }

    public async Task ReloadJavaAsync()
    {
        var runtimes = await _services.Java.DiscoverAsync(Settings.JavaPath);
        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
        {
            JavaRuntimes.Add(runtime);
        }

        OnPropertyChanged(nameof(SelectedJavaText));
    }

    public string SelectedJavaText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.JavaPath) && File.Exists(Settings.JavaPath))
            {
                var runtime = JavaRuntimes.FirstOrDefault(j =>
                    string.Equals(j.ExecutablePath, Settings.JavaPath, StringComparison.OrdinalIgnoreCase));
                if (runtime is not null) return runtime.Display;
            }

            return JavaRuntimes.FirstOrDefault()?.Display ?? "自动选择 / 启动时下载";
        }
    }

    public async Task LaunchAsync(GameInstance? instance = null, string? serverAddress = null, int? serverPort = null)
    {
        instance ??= SelectedInstance;
        if (instance is null)
        {
            Notify("请先创建一个游戏", true);
            return;
        }

        if (IsLaunching)
        {
            return;
        }

        if (ActiveSession?.IsRunning == true)
        {
            Notify("游戏已经在运行中", false);
            ShowLaunchOverlay = true;
            return;
        }

        if (IsRoomActive && ActiveRoom is not null && ActiveRoom.Role == RoomRole.Host)
        {
            // A hosted LAN room is independent from the game process; no special handling needed.
        }

        IsLaunching = true;
        LaunchError = null;
        LaunchTitle = $"正在启动 {instance.Name}";
        LaunchDetail = "准备启动参数…";
        LaunchFraction = 0;
        ShowLaunchOverlay = true;
        try
        {
            string? authlibJar = null;
            if (_activeAuthlib is not null)
            {
                LaunchDetail = "准备第三方登录组件…";
                authlibJar = await _services.Authlib.EnsureInjectorAsync();
            }

            var options = new LaunchOptions
            {
                Player = Player,
                MemoryMb = Settings.MemoryMb,
                Width = 1280,
                Height = 720,
                ServerAddress = serverAddress,
                ServerPort = serverPort,
                AuthlibServer = _activeAuthlib?.Server,
                AuthlibJar = authlibJar
            };
            var progress = new Progress<LaunchProgress>(p =>
            {
                LaunchTitle = $"正在启动 {instance.Name}";
                LaunchDetail = $"{p.Stage} · {p.Detail}";
                LaunchFraction = Math.Clamp(p.Fraction, 0, 1);
            });
            var session = await _services.Launch.LaunchAsync(instance, options, progress);
            ActiveSession = session;
            LaunchTitle = "游戏已启动";
            LaunchDetail = $"PID {session.ProcessId} · 游戏正在加载";
            LaunchFraction = 1;
            session.Exited += code => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                Notify(code == 0 ? "游戏已退出" : $"游戏异常退出（代码 {code}）", code != 0);
                var finished = ActiveSession;
                ActiveSession = null;
                ShowLaunchOverlay = false;
                if (finished is not null)
                {
                    // Dispose away from the Process.Exited callback. Disposing a Process
                    // synchronously while its exit callback is still running can deadlock.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        try { finished.Dispose(); } catch { }
                    });
                }
            }));
            await _services.Instances.MarkPlayedAsync(instance);
            await RefreshInstancesAsync(instance.Id);
            IsLaunching = false;
            Notify($"已启动 {instance.Name}", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("启动游戏失败", ex);
            IsLaunching = false;
            LaunchTitle = "启动失败";
            LaunchDetail = ex.Message;
            LaunchError = ex.ToString();
            LaunchFraction = 0;
            ShowLaunchOverlay = true;
            Notify("启动失败：" + ex.Message, true);
        }
    }

    public void ShowActiveGameOverlay()
    {
        if (HasActiveGame || IsLaunching || HasLaunchError)
        {
            ShowLaunchOverlay = true;
        }
    }

    public void DismissLaunchOverlay()
    {
        ShowLaunchOverlay = false;
        if (HasLaunchError)
        {
            LaunchError = null;
        }
    }

    public void StopGame()
    {
        ActiveSession?.Stop();
        Notify("正在结束游戏进程…", false);
    }

    public async Task HostRoomAsync(int gamePort)
    {
        if (IsRoomBusy || IsRoomActive)
        {
            Notify("你已经在一个联机房间中", true);
            return;
        }

        IsRoomBusy = true;
        RoomStatus = "正在准备联机模块…";
        RoomProgress = 0;
        try
        {
            var progress = new Progress<RoomProgress>(p =>
            {
                RoomStatus = p.Detail;
                RoomProgress = Math.Clamp(p.Fraction, 0, 1);
            });
            var session = await _services.Rooms.HostAsync(gamePort, progress);
            ActiveRoom = session;
            RoomInviteCode = session.Invite.Encode();
            RoomConnectAddress = null;
            RoomRoleText = "房主";
            RoomStatus = "房间已创建，等待朋友加入";
            RoomProgress = 1;
            Settings.LastRoomInvite = RoomInviteCode;
            await _services.Settings.SaveAsync();
            session.Exited += code => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                Notify($"联机模块已退出（{code}）", code != 0);
                var finished = ActiveRoom;
                ActiveRoom = null;
                RoomInviteCode = null;
                RoomStatus = "联机模块已退出";
                if (finished is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        try { finished.Dispose(); } catch { }
                    });
                }
            }));
            Notify("房间已创建，邀请码已准备好", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("创建联机房间失败", ex);
            RoomStatus = "创建房间失败：" + ex.Message;
            Notify("创建房间失败：" + ex.Message, true);
        }
        finally
        {
            IsRoomBusy = false;
        }
    }

    public async Task JoinRoomAsync(string code)
    {
        if (IsRoomBusy || IsRoomActive)
        {
            Notify("你已经在一个联机房间中", true);
            return;
        }

        IsRoomBusy = true;
        RoomStatus = "正在解析邀请码…";
        RoomProgress = 0;
        try
        {
            var progress = new Progress<RoomProgress>(p =>
            {
                RoomStatus = p.Detail;
                RoomProgress = Math.Clamp(p.Fraction, 0, 1);
            });
            var session = await _services.Rooms.JoinAsync(code, progress);
            ActiveRoom = session;
            RoomInviteCode = null;
            RoomConnectAddress = session.ConnectAddress;
            RoomRoleText = "成员";
            RoomStatus = "已加入房间，可以在多人游戏中连接";
            RoomProgress = 1;
            Settings.LastRoomInvite = code.Trim();
            await _services.Settings.SaveAsync();
            session.Exited += exitCode => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                Notify($"联机模块已退出（{exitCode}）", exitCode != 0);
                var finished = ActiveRoom;
                ActiveRoom = null;
                RoomConnectAddress = null;
                RoomStatus = "联机模块已退出";
                if (finished is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        try { finished.Dispose(); } catch { }
                    });
                }
            }));
            Notify("已加入联机房间", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("加入联机房间失败", ex);
            RoomStatus = "加入房间失败：" + ex.Message;
            Notify("加入房间失败：" + ex.Message, true);
        }
        finally
        {
            IsRoomBusy = false;
        }
    }

    public async Task HostDirectAsync(int gamePort)
    {
        if (IsDirectBusy || IsDirectActive)
        {
            Notify("你已经在 IPv6 直连中", true);
            return;
        }

        if (IsRoomActive)
        {
            Notify("请先离开 EasyTier 房间，再创建 IPv6 直连", true);
            return;
        }

        IsDirectBusy = true;
        DirectStatus = "正在准备 IPv6 直连…";
        DirectProgress = 0;
        try
        {
            var progress = new Progress<Ipv6DirectProgress>(p =>
            {
                DirectStatus = p.Detail;
                DirectProgress = Math.Clamp(p.Fraction, 0, 1);
            });
            _directSession = await _services.DirectRooms.HostAsync(gamePort, progress);
            DirectShareAddress = _directSession.ShareAddress;
            DirectConnectAddress = null;
            DirectRoleText = "房主";
            DirectStatus = "IPv6 直连已开启，把地址发给朋友";
            DirectProgress = 1;
            OnPropertyChanged(nameof(IsDirectActive));
            Notify("IPv6 直连已开启", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("创建 IPv6 直连失败", ex);
            DirectStatus = "创建失败：" + ex.Message;
            Notify("创建 IPv6 直连失败：" + ex.Message, true);
        }
        finally
        {
            IsDirectBusy = false;
        }
    }

    public async Task JoinDirectAsync(string endpoint)
    {
        if (IsDirectBusy || IsDirectActive)
        {
            Notify("你已经在 IPv6 直连中", true);
            return;
        }

        if (IsRoomActive)
        {
            Notify("请先离开 EasyTier 房间，再加入 IPv6 直连", true);
            return;
        }

        IsDirectBusy = true;
        DirectStatus = "正在连接房主…";
        DirectProgress = 0;
        try
        {
            var progress = new Progress<Ipv6DirectProgress>(p =>
            {
                DirectStatus = p.Detail;
                DirectProgress = Math.Clamp(p.Fraction, 0, 1);
            });
            _directSession = await _services.DirectRooms.JoinAsync(endpoint, progress);
            DirectShareAddress = null;
            DirectConnectAddress = _directSession.ConnectAddress;
            DirectRoleText = "成员";
            DirectStatus = "已连接，在多人游戏中连接下面的地址";
            DirectProgress = 1;
            OnPropertyChanged(nameof(IsDirectActive));
            Notify("已加入 IPv6 直连", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("加入 IPv6 直连失败", ex);
            DirectStatus = "连接失败：" + ex.Message;
            Notify("加入 IPv6 直连失败：" + ex.Message, true);
        }
        finally
        {
            IsDirectBusy = false;
        }
    }

    public async Task StopDirectAsync()
    {
        var session = _directSession;
        if (session is null) return;
        await session.StopAsync();
        session.Dispose();
        _directSession = null;
        DirectShareAddress = null;
        DirectConnectAddress = null;
        DirectRoleText = "";
        DirectStatus = "已断开 IPv6 直连";
        DirectProgress = 0;
        OnPropertyChanged(nameof(IsDirectActive));
        Notify("已断开 IPv6 直连", false);
    }

    public async Task StopRoomAsync()
    {
        var room = ActiveRoom;
        if (room is null)
        {
            return;
        }

        await room.StopAsync();
        room.Dispose();
        ActiveRoom = null;
        RoomInviteCode = null;
        RoomConnectAddress = null;
        RoomStatus = "已离开房间";
        RoomProgress = 0;
        Notify("已离开联机房间", false);
    }

    public async Task SetPlayerNameAsync(string name)
    {
        var profile = PlayerProfile.Offline(name);
        Settings.LastPlayerName = profile.Name;
        if (_activeAuthlib is not null)
        {
            _activeAuthlib = null;
            _accountFile.ActiveKey = null;
            await _services.AuthlibStore.SaveAsync(_accountFile);
            OnPropertyChanged(nameof(ActiveAuthlibAccount));
            OnPropertyChanged(nameof(IsAuthlibActive));
            OnPropertyChanged(nameof(AccountSubtitle));
        }

        PlayerName = profile.Name;
        await _services.Settings.SaveAsync();
        Notify("玩家名称已更新", false);
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            await _services.Settings.SaveAsync();
            ThemeService.Apply(Settings.ThemeMode, Settings.AccentTheme);
            if (_activeAuthlib is null)
            {
                PlayerName = Settings.LastPlayerName;
            }

            await RefreshInstancesAsync();
            await ReloadJavaAsync();
            OnPropertyChanged(nameof(SelectedJavaText));
            Notify("设置已保存", false);
        }
        catch (Exception ex)
        {
            _services.Log.Error("保存设置失败", ex);
            Notify("保存设置失败：" + ex.Message, true);
        }
    }

    public static string ExportDirectory => @"D:\MCLneo\exports";

    public void Notify(string message, bool error)
    {
        ToastRequested?.Invoke(message, error);
    }

    public void Dispose()
    {
        try { ActiveRoom?.Dispose(); } catch { }
        try { _directSession?.Dispose(); } catch { }
        // Do not dispose ActiveSession here: if the game is still running the user
        // expects it to keep running, and Process.Dispose during an exit callback
        // can block the UI thread. The OS closes our handle when the process exits.
        _services.Settings.SaveSynchronously();
        _services.Dispose();
    }
}
