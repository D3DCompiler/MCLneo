using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MCLneo.Core.Rooms;

public sealed record Ipv6DirectProgress(string Stage, string Detail, double Fraction = 0);

/// <summary>
/// A tiny TCP relay for IPv6 direct play. Minecraft's LAN world is normally
/// IPv4-only and the client's direct-connect field is unreliable with raw IPv6
/// literals, so the host exposes an IPv6 listener and the guest gets a local
/// 127.0.0.1 endpoint that tunnels to that listener.
/// </summary>
public sealed class Ipv6DirectService
{
    public async Task<Ipv6DirectSession> HostAsync(
        int gamePort,
        IProgress<Ipv6DirectProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (gamePort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePort), "端口必须在 1024–65535 之间");
        }

        progress?.Report(new Ipv6DirectProgress("IPv6 直连", "正在查找公网 IPv6 地址", 0.15));
        var address = FindGlobalIpv6()
                      ?? throw new InvalidOperationException(
                          "没有检测到公网 IPv6 地址。请确认网络支持 IPv6，并关闭临时/隐私地址后重试。");
        var listener = new TcpListener(IPAddress.IPv6Any, gamePort);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"无法监听 IPv6 端口 {gamePort}。请确认端口未被占用，并允许 Windows 防火墙放行。", ex);
        }

        progress?.Report(new Ipv6DirectProgress("IPv6 直连", "正在等待朋友连接", 1));
        var session = new Ipv6DirectSession(
            listener,
            new IPEndPoint(IPAddress.Loopback, gamePort),
            $"[{address}]:{gamePort}",
            null);
        session.StartAccepting();
        return session;
    }

    public async Task<Ipv6DirectSession> JoinAsync(
        string endpoint,
        IProgress<Ipv6DirectProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!TryParseEndpoint(endpoint, out var host, out var port))
        {
            throw new ArgumentException("地址格式应为 [IPv6]:端口，例如 [2408:xxxx::1]:54321", nameof(endpoint));
        }

        progress?.Report(new Ipv6DirectProgress("IPv6 直连", "正在测试房主连接", 0.2));
        using (var probe = new TcpClient(AddressFamily.InterNetworkV6))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await probe.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("连接房主超时。请确认房主在线、IPv6 地址正确，并已放行防火墙。");
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("无法连接房主 IPv6 地址：" + ex.Message, ex);
            }
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var remote = new IPEndPoint(host, port);
        progress?.Report(new Ipv6DirectProgress("IPv6 直连", "正在创建本地入口", 0.8));
        var session = new Ipv6DirectSession(listener, remote, null, localPort);
        session.StartAccepting();
        progress?.Report(new Ipv6DirectProgress("IPv6 直连", "已就绪", 1));
        return session;
    }

    public static bool TryParseEndpoint(string? value, out IPAddress host, out int port)
    {
        host = IPAddress.IPv6None;
        port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0 || close + 2 > text.Length || text[close + 1] != ':') return false;
            if (!IPAddress.TryParse(text[1..close], out var parsed) ||
                parsed.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (!int.TryParse(text[(close + 2)..], out port) || port is < 1024 or > 65535) return false;
            host = parsed;
            return true;
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out port) || port is < 1024 or > 65535)
        {
            return false;
        }

        if (!IPAddress.TryParse(text[..colon], out var raw) ||
            raw.AddressFamily != AddressFamily.InterNetworkV6) return false;
        host = raw;
        return true;
    }

    public static string? FindGlobalIpv6()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                var address = info.Address;
                if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
                    address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                {
                    continue;
                }

                if (info.PrefixLength > 64)
                {
                    continue;
                }

                var text = address.ToString();
                // Documentation / ULA ranges are not globally reachable.
                if (text.StartsWith("fc", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("fd", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return text;
            }
        }

        return null;
    }
}

public sealed class Ipv6DirectSession : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();
    private bool _stopped;

    internal Ipv6DirectSession(TcpListener listener, IPEndPoint target, string? shareAddress, int? localPort)
    {
        _listener = listener;
        _target = target;
        ShareAddress = shareAddress;
        ConnectAddress = localPort is > 0 ? $"127.0.0.1:{localPort}" : null;
    }

    public string? ShareAddress { get; }
    public string? ConnectAddress { get; }
    public bool IsRunning => !_stopped;

    internal void StartAccepting()
    {
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                lock (_clients) _clients.Add(client);
                _ = Task.Run(() => ForwardAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var server = new TcpClient(_target.AddressFamily);
                await server.ConnectAsync(_target, _cts.Token).ConfigureAwait(false);
                await using var clientStream = client.GetStream();
                await using var serverStream = server.GetStream();
                var up = clientStream.CopyToAsync(serverStream, _cts.Token);
                var down = serverStream.CopyToAsync(clientStream, _cts.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                lock (_clients) _clients.Remove(client);
            }
        }
    }

    public Task StopAsync()
    {
        if (_stopped) return Task.CompletedTask;
        _stopped = true;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        lock (_clients)
        {
            foreach (var client in _clients.ToList())
            {
                try { client.Dispose(); } catch { }
            }

            _clients.Clear();
        }

        _cts.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
