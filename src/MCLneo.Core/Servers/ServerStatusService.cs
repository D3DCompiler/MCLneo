using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MCLneo.Core.Servers;

/// <summary>
/// Result of a Minecraft Server List Ping (1.7+).
/// <see cref="Players"/> is the sample published by the server and may be
/// empty even when players are online; the server can hide the sample.
/// </summary>
public sealed record ServerStatus(
    bool Online,
    int OnlinePlayers,
    int MaxPlayers,
    IReadOnlyList<string> Players,
    string Version,
    string Motd,
    string? Error = null)
{
    public static ServerStatus Offline(string error) =>
        new(false, 0, 0, Array.Empty<string>(), "", "", error);
}

public static class ServerStatusService
{
    private const int DefaultPort = 25565;
    private const int ProtocolVersion = 765;

    public static async Task<ServerStatus> QueryAsync(
        string host,
        int port = DefaultPort,
        int timeoutMs = 4500,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return ServerStatus.Offline("服务器地址为空");
        }

        if (port is < 1 or > 65535)
        {
            return ServerStatus.Offline("服务器端口无效");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(500, timeoutMs));
        try
        {
            var hostText = host.Trim();
            var parsed = IPAddress.TryParse(hostText, out var address);
            using var client = parsed
                ? new TcpClient(address!.AddressFamily)
                : new TcpClient();
            if (parsed)
            {
                await client.ConnectAsync(address!, port, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await client.ConnectAsync(hostText, port, timeout.Token).ConfigureAwait(false);
            }

            await using var stream = client.GetStream();

            await WritePacketAsync(stream, BuildHandshake(host, port), timeout.Token).ConfigureAwait(false);
            await WritePacketAsync(stream, new byte[] { 0x00 }, timeout.Token).ConfigureAwait(false);

            var length = await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false);
            if (length is <= 0 or > 2 * 1024 * 1024)
            {
                return ServerStatus.Offline("服务器返回了异常数据");
            }

            var packet = new byte[length];
            await stream.ReadExactlyAsync(packet, timeout.Token).ConfigureAwait(false);
            using var packetStream = new MemoryStream(packet);
            var packetId = await ReadVarIntAsync(packetStream, timeout.Token).ConfigureAwait(false);
            if (packetId != 0x00)
            {
                return ServerStatus.Offline("服务器状态响应格式无效");
            }

            var json = await ReadStringAsync(packetStream, timeout.Token).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ServerStatus.Offline("连接超时");
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or
                                       ArgumentException or InvalidDataException or FormatException)
        {
            return ServerStatus.Offline(ex.Message);
        }
    }

    private static byte[] BuildHandshake(string host, int port)
    {
        using var body = new MemoryStream();
        WriteVarInt(body, 0x00);
        WriteVarInt(body, ProtocolVersion);
        WriteString(body, host.Trim());
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        body.Write(portBytes);
        WriteVarInt(body, 1);
        return body.ToArray();
    }

    private static async Task WritePacketAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static ServerStatus Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var version = "";
        if (root.TryGetProperty("version", out var versionElement) &&
            versionElement.TryGetProperty("name", out var nameElement))
        {
            version = nameElement.GetString() ?? "";
        }

        var motd = root.TryGetProperty("description", out var description)
            ? FlattenText(description)
            : "";
        var online = 0;
        var max = 0;
        var players = new List<string>();
        if (root.TryGetProperty("players", out var playersElement))
        {
            if (playersElement.TryGetProperty("online", out var onlineElement) &&
                onlineElement.TryGetInt32(out var onlineValue))
            {
                online = Math.Max(0, onlineValue);
            }

            if (playersElement.TryGetProperty("max", out var maxElement) &&
                maxElement.TryGetInt32(out var maxValue))
            {
                max = Math.Max(0, maxValue);
            }

            if (playersElement.TryGetProperty("sample", out var sample) &&
                sample.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sample.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("name", out var playerName))
                    {
                        var name = playerName.GetString();
                        if (!string.IsNullOrWhiteSpace(name) && players.Count < 100)
                        {
                            players.Add(name);
                        }
                    }
                }
            }
        }

        return new ServerStatus(true, online, max, players, version, motd);
    }

    private static string FlattenText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Object => BuildComponent(element),
            JsonValueKind.Array => string.Join("", element.EnumerateArray().Select(FlattenText)),
            _ => ""
        };
    }

    private static string BuildComponent(JsonElement element)
    {
        var builder = new StringBuilder();
        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            builder.Append(text.GetString());
        }

        if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in extra.EnumerateArray())
            {
                builder.Append(FlattenText(part));
            }
        }

        return builder.ToString();
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;
        do
        {
            var b = (byte)(unsigned & 0x7F);
            unsigned >>= 7;
            if (unsigned != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (unsigned != 0);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("连接已被服务器关闭");
            var b = buffer[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }

        throw new InvalidDataException("VarInt 过长");
    }

    private static async Task<string> ReadStringAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length is < 0 or > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("字符串长度异常");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
