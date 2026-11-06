using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using MCLneo.Core.Infrastructure;

namespace MCLneo.Core.Rooms;

public sealed record RoomProgress(string Stage, string Detail, double Fraction = 0);

public sealed class EasyTierManager
{
    private const string FallbackVersion = "v2.6.4";
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly HttpService _http;
    private readonly AppLog _log;

    public EasyTierManager(
        AppPaths paths,
        SettingsService settings,
        DownloadService downloads,
        HttpService http,
        AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _downloads = downloads;
        _http = http;
        _log = log;
    }

    public string CorePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.Current.EasyTierCorePath) &&
                File.Exists(_settings.Current.EasyTierCorePath))
            {
                return Path.GetFullPath(_settings.Current.EasyTierCorePath);
            }

            return Path.Combine(_paths.EasyTierDirectory, "easytier-core.exe");
        }
    }

    public async Task<string> EnsureInstalledAsync(
        IProgress<RoomProgress>? progress = null,
        CancellationToken ct = default)
    {
        var existing = CorePath;
        if (File.Exists(existing))
        {
            progress?.Report(new RoomProgress("联机模块", "EasyTier 已就绪", 1));
            return existing;
        }

        Directory.CreateDirectory(_paths.EasyTierDirectory);
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "i686",
            _ => "x86_64"
        };

        progress?.Report(new RoomProgress("联机模块", "正在查询 EasyTier 版本", 0.05));
        var assetUrl = await FindReleaseAssetAsync(arch, ct).ConfigureAwait(false);
        var archive = Path.Combine(_paths.CacheDirectory, $"easytier-windows-{arch}.zip");
        progress?.Report(new RoomProgress("联机模块", "正在下载 EasyTier", 0.1));
        await _downloads.DownloadFileAsync(new DownloadItem(assetUrl, archive, null, -1,
            $"EasyTier ({arch})"), null, ct).ConfigureAwait(false);

        progress?.Report(new RoomProgress("联机模块", "正在解压", 0.7));
        var temp = Path.Combine(_paths.TempDirectory, "easytier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, temp, true), ct).ConfigureAwait(false);
            var core = Directory.EnumerateFiles(temp, "easytier-core.exe", SearchOption.AllDirectories).FirstOrDefault()
                       ?? throw new InvalidDataException("EasyTier 压缩包中缺少 easytier-core.exe");
            var sourceDir = Path.GetDirectoryName(core)!;
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(_paths.EasyTierDirectory, Path.GetFileName(file));
                File.Copy(file, target, true);
            }

            var targetCore = Path.Combine(_paths.EasyTierDirectory, "easytier-core.exe");
            if (!File.Exists(targetCore))
            {
                throw new InvalidDataException("EasyTier 安装失败");
            }

            _log.Info($"EasyTier 已安装：{targetCore}");
            progress?.Report(new RoomProgress("联机模块", "EasyTier 就绪", 1));
            return targetCore;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
            catch
            {
            }
        }
    }

    public async Task<string> ResolvePeerAsync(IProgress<RoomProgress>? progress = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_settings.Current.EasyTierPeer))
        {
            var configured = _settings.Current.EasyTierPeer.Trim();
            if (RoomInvite.IsValidPeer(configured))
            {
                return configured;
            }
        }

        progress?.Report(new RoomProgress("联机模块", "正在选择公共中继", 0.4));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var json = await _http.GetStringAsync(
                "https://uptime.easytier.cn/api/nodes?page=1&per_page=200", timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.TryGetProperty("data", out var data) && data.TryGetProperty("items", out var nodes)
                ? nodes
                : default;
            if (items.ValueKind == JsonValueKind.Array)
            {
                var candidates = new List<(string Address, double Load, bool Relay)>();
                foreach (var node in items.EnumerateArray())
                {
                    if (!GetBool(node, "is_active") || !GetBool(node, "is_approved"))
                    {
                        continue;
                    }

                    var address = GetString(node, "address");
                    if (string.IsNullOrWhiteSpace(address) || !RoomInvite.IsValidPeer(address))
                    {
                        continue;
                    }

                    var tags = GetStrings(node, "tags");
                    var isMc = tags.Any(t => t.Contains("MC中继", StringComparison.OrdinalIgnoreCase) ||
                                             t.Contains("MC", StringComparison.OrdinalIgnoreCase));
                    var isDomestic = tags.Any(t => t.Contains("国内", StringComparison.OrdinalIgnoreCase));
                    if (!isMc || !isDomestic)
                    {
                        continue;
                    }

                    var load = node.TryGetProperty("usage_percentage", out var l) && l.TryGetDouble(out var loadValue)
                        ? loadValue
                        : 100;
                    var relay = GetBool(node, "allow_relay");
                    candidates.Add((address, load, relay));
                }

                var selected = candidates
                    .OrderByDescending(c => c.Relay)
                    .ThenBy(c => c.Load)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(selected.Address))
                {
                    _log.Info($"EasyTier 中继：{selected.Address}");
                    return selected.Address;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("获取公共中继列表失败，使用备用节点", ex);
        }

        return "tcp://public.easytier.top:11010";
    }

    private async Task<string> FindReleaseAssetAsync(string arch, CancellationToken ct)
    {
        try
        {
            using var document = await _http.GetJsonAsync(
                "https://api.github.com/repos/EasyTier/EasyTier/releases/latest", ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.StartsWith($"easytier-windows-{arch}-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(url)) return url;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("GitHub Release API 不可用，使用固定版本下载地址", ex);
        }

        return $"https://github.com/EasyTier/EasyTier/releases/download/{FallbackVersion}/" +
               $"easytier-windows-{arch}-{FallbackVersion}.zip";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static List<string> GetStrings(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? "").ToList()
            : new List<string>();
    }
}

public sealed class RoomSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;

    internal RoomSession(Process process, RoomRole role, RoomInvite invite, int? localPort, string logPath,
        StreamWriter writer)
    {
        _process = process;
        Role = role;
        Invite = invite;
        LocalPort = localPort;
        LogPath = logPath;
        _writer = writer;
    }

    public RoomRole Role { get; }
    public RoomInvite Invite { get; }
    public int? LocalPort { get; }
    public string LogPath { get; }
    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public string? ConnectAddress => LocalPort is > 0 ? $"127.0.0.1:{LocalPort}" : null;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    internal void Attach()
    {
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.Exited += (_, _) =>
        {
            try
            {
                _writer.WriteLine($"[MCLneo] EasyTier 已退出（{_process.ExitCode}）");
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
            }

            try { Exited?.Invoke(_process.ExitCode); } catch { }
        };
        _process.EnableRaisingEvents = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Append(string? line)
    {
        if (line is null) return;
        try
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
        catch
        {
        }

        try { OutputReceived?.Invoke(line); } catch { }
    }

    public async Task StopAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try { _process.Kill(true); } catch { }
        _process.Dispose();
    }
}

public sealed class RoomService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly EasyTierManager _easyTier;
    private readonly AppLog _log;

    public RoomService(AppPaths paths, SettingsService settings, EasyTierManager easyTier, AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _easyTier = easyTier;
        _log = log;
    }

    public async Task<RoomSession> HostAsync(
        int gamePort,
        IProgress<RoomProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (gamePort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePort), "端口必须在 1024–65535 之间");
        }

        var core = await _easyTier.EnsureInstalledAsync(progress, ct).ConfigureAwait(false);
        progress?.Report(new RoomProgress("创建房间", "正在选择中继节点", 0.2));
        var peer = await _easyTier.ResolvePeerAsync(progress, ct).ConfigureAwait(false);
        var invite = RoomInvite.Create(peer, gamePort);
        var (listenerPort, rpcPort) = AllocateRoomPorts();
        var args = new List<string>
        {
            "--network-name", invite.NetworkName,
            "--network-secret", invite.Secret,
            "--listeners", listenerPort.ToString(),
            "--rpc-portal", rpcPort.ToString(),
            "--private-mode", "true",
            "-i", "10.114.114.114",
            "--hostname", "MCLneo-Host",
            "--tcp-whitelist", gamePort.ToString(),
            "--udp-whitelist", gamePort.ToString(),
            "-p", peer
        };

        progress?.Report(new RoomProgress("创建房间", "正在启动联机模块", 0.45));
        var session = Start(core, args, RoomRole.Host, invite, null);
        await Task.Delay(2500, ct).ConfigureAwait(false);
        if (!session.IsRunning)
        {
            session.Dispose();
            throw new InvalidOperationException(
                "联机模块启动失败。请确认启动器以管理员身份运行（需要创建 TUN 虚拟网卡），并检查日志或更换中继节点。");
        }

        _log.Info($"联机房间已创建：{invite.NetworkName}，端口 {gamePort}，中继 {peer}");
        progress?.Report(new RoomProgress("房间已创建", "把邀请码发给朋友", 1));
        return session;
    }

    public async Task<RoomSession> JoinAsync(
        string code,
        IProgress<RoomProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!RoomInvite.TryDecode(code, out var invite, out var error))
        {
            throw new ArgumentException(error, nameof(code));
        }

        var core = await _easyTier.EnsureInstalledAsync(progress, ct).ConfigureAwait(false);
        var localPort = GetFreePort();
        var (listenerPort, rpcPort) = AllocateRoomPorts();
        var args = new List<string>
        {
            "-d",
            "--network-name", invite.NetworkName,
            "--network-secret", invite.Secret,
            "--listeners", listenerPort.ToString(),
            "--rpc-portal", rpcPort.ToString(),
            "--private-mode", "true",
            "--hostname", "MCLneo-Client",
            "--tcp-whitelist", "0",
            "--udp-whitelist", "0",
            "--port-forward", $"tcp://127.0.0.1:{localPort}/10.114.114.114:{invite.GamePort}",
            "--port-forward", $"udp://127.0.0.1:{localPort}/10.114.114.114:{invite.GamePort}",
            "-p", invite.Peer
        };

        progress?.Report(new RoomProgress("加入房间", "正在连接房主", 0.45));
        var session = Start(core, args, RoomRole.Client, invite, localPort);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!session.IsRunning)
            {
                session.Dispose();
                throw new InvalidOperationException("联机模块意外退出，请检查邀请码或更换中继");
            }

            if (await IsPortOpenAsync(localPort, ct).ConfigureAwait(false))
            {
                _log.Info($"已加入联机房间：{invite.NetworkName}，本地地址 127.0.0.1:{localPort}");
                progress?.Report(new RoomProgress("已加入房间", $"在多人游戏中连接 127.0.0.1:{localPort}", 1));
                return session;
            }

            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            progress?.Report(new RoomProgress("加入房间",
                $"正在等待房主响应（{Math.Max(0, (int)remaining)} 秒）", 0.55));
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        await session.StopAsync().ConfigureAwait(false);
        session.Dispose();
        throw new TimeoutException("连接房主超时。请确认房主仍在房间中，并检查防火墙设置。");
    }

    private RoomSession Start(
        string core,
        IReadOnlyList<string> args,
        RoomRole role,
        RoomInvite invite,
        int? localPort)
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        var logPath = Path.Combine(_paths.LogsDirectory, $"room-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var psi = new ProcessStartInfo(core)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(core) ?? _paths.EasyTierDirectory
        };
        psi.Environment["TMP"] = _paths.TempDirectory;
        psi.Environment["TEMP"] = _paths.TempDirectory;
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var writer = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine($"=== MCLneo 联机 {DateTimeOffset.Now:O} ===");
        writer.WriteLine("参数：" + string.Join(" ", args));
        _log.Info("启动联机模块：" + string.Join(" ", args.Where(a => a != invite.Secret)));
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        if (process is null)
        {
            writer.Dispose();
            throw new InvalidOperationException("无法启动 EasyTier");
        }

        var session = new RoomSession(process, role, invite, localPort, logPath, writer);
        session.Attach();
        return session;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// EasyTier derives ws/quic/faketcp listeners from the base port, so reserve a
    /// contiguous block (base..base+4) and keep the RPC portal outside it.
    /// </summary>
    private static (int ListenerPort, int RpcPort) AllocateRoomPorts()
    {
        var random = Random.Shared;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var basePort = random.Next(20000, 55000);
            if (basePort + 6 > 65535)
            {
                continue;
            }

            var blockFree = true;
            for (var offset = 0; offset < 5; offset++)
            {
                if (!IsTcpPortFree(basePort + offset) || !IsUdpPortFree(basePort + offset))
                {
                    blockFree = false;
                    break;
                }
            }

            if (!blockFree)
            {
                continue;
            }

            for (var attemptRpc = 0; attemptRpc < 40; attemptRpc++)
            {
                var rpc = random.Next(20000, 60000);
                if (rpc >= basePort && rpc < basePort + 5)
                {
                    continue;
                }

                if (IsTcpPortFree(rpc))
                {
                    return (basePort, rpc);
                }
            }
        }

        throw new InvalidOperationException("无法找到可用的联机端口，请关闭占用端口的程序后重试");
    }

    private static bool IsTcpPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsUdpPortFree(int port)
    {
        try
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
