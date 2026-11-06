using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using MCLneo.Core.Accounts;
using MCLneo.Core.Infrastructure;

namespace MCLneo.Core.Skins;

/// <summary>Fetches the active account's skin head for preview; server-side skin editing stays on the auth server.</summary>
public sealed class SkinService
{
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly AppLog _log;

    public SkinService(AppPaths paths, HttpService http, AppLog log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    public async Task<string?> GetSkinAsync(PlayerProfile player, string? authlibServer = null,
        CancellationToken ct = default)
    {
        var cache = Path.Combine(_paths.CacheDirectory, "skins");
        Directory.CreateDirectory(cache);
        var key = player.IsOffline ? "offline-" + PathSafety.SafeFileName(player.Name) : player.Uuid;
        var target = Path.Combine(cache, key + ".png");
        if (File.Exists(target) && DateTime.UtcNow - File.GetLastWriteTimeUtc(target) < TimeSpan.FromHours(12))
        {
            return target;
        }

        try
        {
            string? skinUrl = null;
            if (!player.IsOffline && !string.IsNullOrWhiteSpace(authlibServer))
            {
                var server = AuthlibService.NormalizeServer(authlibServer);
                var profileUrl = $"{server}/sessionserver/session/minecraft/profile/{player.Uuid}";
                var text = await _http.GetStringAsync(profileUrl, ct).ConfigureAwait(false);
                var root = JsonNode.Parse(text);
                var value = (root?["properties"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(p => p["name"]?.GetValue<string>() == "textures")?["value"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                    var texture = JsonNode.Parse(decoded);
                    skinUrl = texture?["textures"]?["SKIN"]?["url"]?.GetValue<string>();
                }
            }

            skinUrl ??= $"https://mc-heads.net/avatar/{Uri.EscapeDataString(player.Name)}/128";
            using var response = await _http.Client.GetAsync(skinUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
                return target;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"获取皮肤预览失败：{player.Name}：{ex.Message}");
        }

        return File.Exists(target) ? target : null;
    }
}
