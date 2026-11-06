using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCLneo.Core.Rooms;

public enum RoomRole
{
    Host,
    Client
}

public sealed class RoomInvite
{
    public string NetworkName { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Peer { get; set; } = "";
    public int GamePort { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public const string Prefix = "MCLN1:";

    public static RoomInvite Create(string peer, int gamePort)
    {
        return new RoomInvite
        {
            NetworkName = "mcl-" + RandomCode(8).ToLowerInvariant(),
            Secret = RandomCode(12),
            Peer = peer,
            GamePort = gamePort,
            CreatedAt = DateTimeOffset.Now
        };
    }

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.Compact);
        return Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? code, out RoomInvite invite, out string error)
    {
        invite = new RoomInvite();
        error = "";
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "邀请码为空";
            return false;
        }

        var normalized = code.Trim().Replace("\r", "").Replace("\n", "").Replace(" ", "");
        var start = normalized.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            error = "邀请码格式不正确，应以 MCLN1: 开头";
            return false;
        }

        normalized = normalized[start..];
        try
        {
            var payload = normalized[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            var parsed = JsonSerializer.Deserialize<RoomInvite>(bytes, JsonDefaults.Options);
            if (parsed is null)
            {
                error = "邀请码内容为空";
                return false;
            }

            if (!IsSafeToken(parsed.NetworkName, 3, 64))
            {
                error = "邀请码中的网络名称无效";
                return false;
            }

            if (!IsSafeToken(parsed.Secret, 6, 128))
            {
                error = "邀请码中的房间密钥无效";
                return false;
            }

            if (!IsValidPeer(parsed.Peer))
            {
                error = "邀请码中的中继地址无效";
                return false;
            }

            if (parsed.GamePort is < 1024 or > 65535)
            {
                error = "邀请码中的端口无效";
                return false;
            }

            invite = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = "邀请码已损坏，请让房主重新复制";
            return false;
        }
    }

    public static bool IsValidPeer(string peer)
    {
        if (string.IsNullOrWhiteSpace(peer) || !Uri.TryCreate(peer, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "tcp" or "udp" or "ws" or "wss" or "wg" &&
               !string.IsNullOrWhiteSpace(uri.Host) && uri.Port is > 0 and <= 65535;
    }

    private static bool IsSafeToken(string value, int min, int max) =>
        value.Length >= min && value.Length <= max &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string RandomCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var builder = new StringBuilder(length);
        foreach (var b in bytes)
        {
            builder.Append(alphabet[b % alphabet.Length]);
        }

        return builder.ToString();
    }
}
