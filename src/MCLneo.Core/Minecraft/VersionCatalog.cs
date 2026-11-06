using System.Text.Json;
using System.Text.Json.Nodes;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Minecraft;

public sealed class VersionCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly AppLog _log;

    public VersionCatalog(AppPaths paths, HttpService http, AppLog log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    public async Task<VersionManifest> GetManifestAsync(
        DownloadSource source,
        bool force = false,
        CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        var cacheFile = Path.Combine(_paths.CacheDirectory, $"version-manifest-{source}.json");
        if (!force && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheLifetime)
        {
            try
            {
                return ParseManifest(await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _log.Warn("版本清单缓存损坏，将重新下载", ex);
            }
        }

        try
        {
            var text = await _http.GetStringAsync(source.ManifestUrl(), ct).ConfigureAwait(false);
            Directory.CreateDirectory(_paths.CacheDirectory);
            await File.WriteAllTextAsync(cacheFile, text, ct).ConfigureAwait(false);
            return ParseManifest(text);
        }
        catch (Exception ex) when (File.Exists(cacheFile))
        {
            _log.Warn("获取版本清单失败，使用过期缓存", ex);
            return ParseManifest(await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false));
        }
    }

    public async Task<JsonObject> GetVersionJsonAsync(
        MinecraftVersionInfo version,
        DownloadSource source,
        CancellationToken ct = default)
    {
        var url = source.VersionJsonUrl(version.Id, version.Url);
        var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException($"版本 JSON 格式无效：{version.Id}");
    }

    public static IReadOnlyList<MinecraftVersionInfo> Filter(
        IEnumerable<MinecraftVersionInfo> versions,
        string? query,
        bool includeSnapshots)
    {
        var list = versions.Where(v => includeSnapshots || v.Type == "release");
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            list = list.Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return list.ToList();
    }

    private static VersionManifest ParseManifest(string text)
    {
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException("版本清单格式无效");
        var latest = root["latest"] as JsonObject;
        var latestRelease = latest?["release"]?.GetValue<string>();
        var latestSnapshot = latest?["snapshot"]?.GetValue<string>();
        var items = new List<MinecraftVersionInfo>();
        if (root["versions"] is JsonArray array)
        {
            foreach (var node in array.OfType<JsonObject>())
            {
                var id = node["id"]?.GetValue<string>();
                var url = node["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var type = node["type"]?.GetValue<string>() ?? "release";
                var releaseTime = node["releaseTime"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue;
                items.Add(new MinecraftVersionInfo(id, type, url, releaseTime,
                    id == latestRelease, id == latestSnapshot));
            }
        }

        return new VersionManifest
        {
            Root = root,
            Versions = items,
            FetchedAt = DateTimeOffset.Now
        };
    }
}
