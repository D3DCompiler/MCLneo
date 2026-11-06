using System.Security.Cryptography;
using System.Text;

namespace MCLneo.Core.Accounts;

public sealed record PlayerProfile(
    string Name,
    string Uuid,
    string AccessToken,
    string UserType = "legacy",
    string? Xuid = null,
    string? ClientId = null,
    bool IsOffline = true)
{
    public string UuidDisplay => Guid.TryParseExact(Uuid, "N", out var guid)
        ? guid.ToString()
        : Uuid;

    public static PlayerProfile Offline(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > 16)
        {
            throw new ArgumentException("玩家名长度必须为 1–16 个字符", nameof(name));
        }

        if (trimmed.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException("玩家名只能包含英文字母、数字和下划线", nameof(name));
        }

        return new PlayerProfile(trimmed, CreateOfflineUuid(trimmed), "0", "legacy", IsOffline: true);
    }

    /// <summary>Java-style UUID.nameUUIDFromBytes("OfflinePlayer:name").</summary>
    public static string CreateOfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true).ToString("N");
    }
}
