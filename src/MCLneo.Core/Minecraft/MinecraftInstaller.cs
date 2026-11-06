using System.Text.Json;
using System.Text.Json.Nodes;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Minecraft;

public sealed record InstallRequest(
    MinecraftVersionInfo GameVersion,
    string InstanceName,
    LoaderKind Loader = LoaderKind.Vanilla,
    string? LoaderVersion = null);

public sealed class MinecraftInstaller
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly VersionCatalog _catalog;
    private readonly HttpService _http;
    private readonly AppLog _log;

    public MinecraftInstaller(
        AppPaths paths,
        SettingsService settings,
        DownloadService downloads,
        VersionCatalog catalog,
        HttpService http,
        AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _downloads = downloads;
        _catalog = catalog;
        _http = http;
        _log = log;
    }

    public string VersionsRoot => Path.Combine(_settings.Current.GameRoot, "versions");

    public async Task<GameInstance> InstallAsync(
        InstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Loader) ||
            request.Loader is LoaderKind.Forge or LoaderKind.NeoForge)
        {
            throw new NotSupportedException(
                $"{request.Loader} 安装器尚未接入。当前里程碑支持原版、Fabric 与 Quilt。");
        }

        var source = _settings.Current.DownloadSource;
        var gameId = request.GameVersion.Id;
        Directory.CreateDirectory(VersionsRoot);

        Report(progress, "准备安装", 0.01, $"Minecraft {gameId}");
        var instanceId = GetUniqueInstanceId(request.InstanceName);
        var instanceDir = Path.Combine(VersionsRoot, instanceId);
        Directory.CreateDirectory(instanceDir);

        await EnsureVanillaAsync(request.GameVersion, source,
            new ScaledProgress(progress, 0.02, 0.66), ct).ConfigureAwait(false);

        JsonObject profile;
        string? loaderVersion = null;
        if (request.Loader == LoaderKind.Vanilla)
        {
            Report(progress, "写入游戏配置", 0.74, "正在建立独立游戏目录");
            // Keep the instance profile minimal. Copying the whole base document would
            // duplicate libraries/arguments when VersionResolver merges the parent.
            profile = new JsonObject
            {
                ["id"] = instanceId,
                ["inheritsFrom"] = gameId,
                ["type"] = "release"
            };
        }
        else
        {
            Report(progress, $"准备 {request.Loader}", 0.70, "正在获取加载器元数据");
            (profile, loaderVersion) = await BuildLoaderProfileAsync(
                request.Loader, gameId, request.LoaderVersion, instanceId, source,
                new ScaledProgress(progress, 0.70, 0.90), ct).ConfigureAwait(false);
        }

        var profilePath = Path.Combine(instanceDir, instanceId + ".json");
        await PathSafety.WriteJsonAtomicAsync(profilePath, profile, ct).ConfigureAwait(false);

        var instance = new GameInstance
        {
            Id = instanceId,
            Name = string.IsNullOrWhiteSpace(request.InstanceName) ? instanceId : request.InstanceName.Trim(),
            Directory = instanceDir,
            MinecraftVersion = gameId,
            Loader = request.Loader,
            LoaderVersion = loaderVersion,
            CreatedAt = DateTimeOffset.Now,
            LastPlayed = DateTimeOffset.Now
        };
        instance.EnsureDirectories();
        await SaveMetadataAsync(instance, ct).ConfigureAwait(false);

        Report(progress, "安装完成", 1, $"{instance.Name} 已准备好");
        _log.Info($"游戏安装完成：{instance.Name}（{instance.Id}，{instance.Subtitle}）");
        return instance;
    }

    /// <summary>Installs the shared vanilla base version into versions/&lt;id&gt;.</summary>
    public async Task EnsureVanillaAsync(
        MinecraftVersionInfo game,
        DownloadSource source,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var versionDir = Path.Combine(VersionsRoot, game.Id);
        var jsonPath = Path.Combine(versionDir, game.Id + ".json");
        var jarPath = Path.Combine(versionDir, game.Id + ".jar");
        Directory.CreateDirectory(versionDir);

        if (File.Exists(jsonPath) && File.Exists(jarPath))
        {
            try
            {
                var existing = await ReadJsonAsync(jsonPath, ct).ConfigureAwait(false);
                if (existing["inheritsFrom"] is null && IsVanillaAssetIndexPresent(existing))
                {
                    var existingLibraries = LibraryResolver.Resolve(existing, source);
                    var librariesPresent = existingLibraries.All(lib =>
                        File.Exists(LibraryResolver.GetLibraryPath(_settings.Current.GameRoot, lib)));
                    if (librariesPresent)
                    {
                        Report(progress, "检查基础版本", 0.70, $"Minecraft {game.Id} 已存在，跳过重复下载");
                        TouchBaseMarker(versionDir);
                        return;
                    }

                    _log.Warn($"基础版本运行库不完整，将重新检查：{game.Id}");
                }
                else
                {
                    _log.Warn($"基础版本资源索引缺失，将重新检查：{game.Id}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"现有基础版本 JSON 损坏，将重新安装：{game.Id}", ex);
            }
        }

        Report(progress, "获取版本信息", 0.01, $"Minecraft {game.Id}");
        var versionJson = await _catalog.GetVersionJsonAsync(game, source, ct).ConfigureAwait(false);
        versionJson["id"] = game.Id;
        await PathSafety.WriteJsonAtomicAsync(jsonPath, versionJson, ct).ConfigureAwait(false);
        TouchBaseMarker(versionDir);

        var downloads = versionJson["downloads"] as JsonObject;
        var clientUrl = downloads?["client"]?["url"]?.GetValue<string>();
        var clientSha1 = downloads?["client"]?["sha1"]?.GetValue<string>();
        var clientSize = downloads?["client"]?["size"]?.GetValue<long>() ?? -1;
        if (string.IsNullOrWhiteSpace(clientUrl))
        {
            throw new InvalidDataException($"Minecraft {game.Id} 的版本信息缺少客户端下载地址");
        }

        Report(progress, "下载游戏核心", 0.05, $"{game.Id}.jar");
        await _downloads.DownloadFileAsync(new DownloadItem(
            source.ClientJarUrl(game.Id, clientUrl),
            jarPath,
            clientSha1,
            clientSize,
            $"{game.Id}.jar"), null, ct).ConfigureAwait(false);

        var libraries = LibraryResolver.Resolve(versionJson, source);
        var libraryItems = libraries.Select(lib => new DownloadItem(
            lib.Url,
            LibraryResolver.GetLibraryPath(_settings.Current.GameRoot, lib),
            lib.Sha1,
            lib.Size,
            Path.GetFileName(lib.RelativePath))).ToList();
        await DownloadWithProgressAsync(libraryItems, "下载运行库", 0.12, 0.48, progress, ct)
            .ConfigureAwait(false);

        var assetIndex = versionJson["assetIndex"] as JsonObject;
        if (assetIndex is not null)
        {
            await EnsureAssetsAsync(assetIndex, source,
                new ScaledProgress(progress, 0.48, 0.94), ct).ConfigureAwait(false);
        }

        if (versionJson["logging"]?["client"]?["file"] is JsonObject logFile)
        {
            var logId = logFile["id"]?.GetValue<string>();
            var logUrl = logFile["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(logId) && !string.IsNullOrWhiteSpace(logUrl))
            {
                var logPath = Path.Combine(_settings.Current.GameRoot, "assets", "log_configs", logId);
                await _downloads.DownloadFileAsync(new DownloadItem(
                    source.Rewrite(logUrl),
                    logPath,
                    logFile["sha1"]?.GetValue<string>(),
                    logFile["size"]?.GetValue<long>() ?? -1,
                    logId), null, ct).ConfigureAwait(false);
            }
        }

        Report(progress, "基础版本就绪", 0.96, $"Minecraft {game.Id}");
    }

    public async Task RepairAsync(
        GameInstance instance,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        instance.EnsureDirectories();
        var source = _settings.Current.DownloadSource;
        Report(progress, "读取版本", 0.02, instance.Name);
        var manifest = await _catalog.GetManifestAsync(source, ct: ct).ConfigureAwait(false);
        var game = manifest.Versions.FirstOrDefault(v => v.Id == instance.MinecraftVersion);
        if (game is not null)
        {
            await EnsureVanillaAsync(game, source, new ScaledProgress(progress, 0.05, 0.5), ct)
                .ConfigureAwait(false);
        }

        var effective = await VersionResolver.LoadEffectiveAsync(instance, ct).ConfigureAwait(false);
        var libraries = LibraryResolver.Resolve(effective, source);
        var missingLocal = libraries
            .Where(l => string.IsNullOrWhiteSpace(l.Url))
            .Where(l => !File.Exists(LibraryResolver.GetLibraryPath(_settings.Current.GameRoot, l)))
            .Select(l => l.Name)
            .ToList();
        if (missingLocal.Count > 0)
        {
            throw new InvalidOperationException(
                "以下加载器文件缺失，需要重新运行对应加载器安装器：\n" + string.Join("\n", missingLocal));
        }

        var items = libraries
            .Where(l => !string.IsNullOrWhiteSpace(l.Url))
            .Select(l => new DownloadItem(l.Url,
                LibraryResolver.GetLibraryPath(_settings.Current.GameRoot, l),
                l.Sha1, l.Size, Path.GetFileName(l.RelativePath)))
            .ToList();
        await DownloadWithProgressAsync(items, "校验运行库", 0.5, 0.92, progress, ct).ConfigureAwait(false);

        var assetsId = effective["assetIndex"]?["id"]?.GetValue<string>()
                       ?? effective["assets"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(assetsId) && assetsId != "legacy" &&
            !File.Exists(Path.Combine(_settings.Current.GameRoot, "assets", "indexes", assetsId + ".json")))
        {
            throw new InvalidOperationException($"资源索引 {assetsId} 缺失，请重新安装基础版本");
        }

        Report(progress, "修复完成", 1, instance.Name);
        _log.Info($"游戏修复完成：{instance.Name}");
    }

    public async Task<string> GetLatestLoaderVersionAsync(
        LoaderKind loader,
        string gameVersion,
        CancellationToken ct = default)
    {
        var url = loader switch
        {
            LoaderKind.Fabric => $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}",
            LoaderKind.Quilt => $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(gameVersion)}",
            _ => throw new NotSupportedException()
        };
        var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("loader", out var loaderNode) &&
                loaderNode.TryGetProperty("version", out var versionNode))
            {
                var stable = loaderNode.TryGetProperty("stable", out var stableNode) && stableNode.GetBoolean();
                if (stable || loader == LoaderKind.Quilt)
                {
                    return versionNode.GetString() ?? "";
                }
            }
        }

        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        return first.TryGetProperty("loader", out var l) && l.TryGetProperty("version", out var v)
            ? v.GetString() ?? ""
            : throw new InvalidDataException("加载器元数据为空");
    }

    private async Task<(JsonObject Profile, string LoaderVersion)> BuildLoaderProfileAsync(
        LoaderKind loader,
        string gameId,
        string? requestedLoaderVersion,
        string instanceId,
        DownloadSource source,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var loaderVersion = string.IsNullOrWhiteSpace(requestedLoaderVersion)
            ? await GetLatestLoaderVersionAsync(loader, gameId, ct).ConfigureAwait(false)
            : requestedLoaderVersion.Trim();

        var profileUrl = loader switch
        {
            LoaderKind.Fabric =>
                $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameId)}/{Uri.EscapeDataString(loaderVersion)}/profile/json",
            LoaderKind.Quilt =>
                $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(gameId)}/{Uri.EscapeDataString(loaderVersion)}/profile/json",
            _ => throw new NotSupportedException()
        };

        Report(progress, $"安装 {loader}", 0.05, $"{loader} {loaderVersion}");
        var text = await _http.GetStringAsync(profileUrl, ct).ConfigureAwait(false);
        var profile = JsonNode.Parse(text) as JsonObject
                      ?? throw new InvalidDataException($"{loader} 配置格式无效");
        profile["id"] = instanceId;
        profile["inheritsFrom"] = gameId;
        profile["type"] = "release";

        var libraries = LibraryResolver.Resolve(profile, source);
        var items = libraries.Select(lib => new DownloadItem(
            lib.Url,
            LibraryResolver.GetLibraryPath(_settings.Current.GameRoot, lib),
            lib.Sha1,
            lib.Size,
            Path.GetFileName(lib.RelativePath))).ToList();
        await DownloadWithProgressAsync(items, $"下载 {loader} 组件", 0.15, 0.85, progress, ct)
            .ConfigureAwait(false);

        return (profile, loaderVersion);
    }

    private async Task EnsureAssetsAsync(
        JsonObject assetIndexNode,
        DownloadSource source,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var id = assetIndexNode["id"]?.GetValue<string>();
        var url = assetIndexNode["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var indexPath = Path.Combine(_settings.Current.GameRoot, "assets", "indexes", id + ".json");
        Report(progress, "下载资源索引", 0, id);
        await _downloads.DownloadFileAsync(new DownloadItem(
            source.Rewrite(url),
            indexPath,
            assetIndexNode["sha1"]?.GetValue<string>(),
            assetIndexNode["size"]?.GetValue<long>() ?? -1,
            id + ".json"), null, ct).ConfigureAwait(false);

        var index = await ReadJsonAsync(indexPath, ct).ConfigureAwait(false);
        if (index["objects"] is not JsonObject objects)
        {
            return;
        }

        var items = new List<DownloadItem>(objects.Count);
        foreach (var (name, node) in objects)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var hash = obj["hash"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
            {
                continue;
            }

            var relative = Path.Combine(hash[..2], hash);
            items.Add(new DownloadItem(
                source.Rewrite($"https://resources.download.minecraft.net/{hash[..2]}/{hash}"),
                Path.Combine(_settings.Current.GameRoot, "assets", "objects", relative),
                hash,
                obj["size"]?.GetValue<long>() ?? -1,
                name));
        }

        await DownloadWithProgressAsync(items, "下载游戏资源", 0.08, 0.95, progress, ct)
            .ConfigureAwait(false);
    }

    private async Task DownloadWithProgressAsync(
        IReadOnlyList<DownloadItem> items,
        string stage,
        double start,
        double end,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        Report(progress, stage, start, $"共 {items.Count} 个文件");
        var inner = new Progress<DownloadProgress>(p =>
        {
            var fraction = p.BytesTotal > 0
                ? Math.Clamp(p.BytesReceived / (double)p.BytesTotal, 0, 1)
                : p.Fraction;
            Report(progress, stage, start + (end - start) * fraction, p.CurrentFile,
                p.Completed, p.Total, p.BytesReceived, p.BytesTotal);
        });
        await _downloads.DownloadBatchAsync(
            items,
            inner,
            _settings.Current.MaxDownloadThreads,
            ct).ConfigureAwait(false);
        Report(progress, stage, end, $"{items.Count} 个文件完成", items.Count, items.Count);
    }

    private async Task<JsonObject> ReadJsonAsync(string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException($"JSON 格式无效：{path}");
    }

    private string GetUniqueInstanceId(string requestedName)
    {
        var baseId = PathSafety.SafeFileName(requestedName, "New-Instance");
        if (baseId.Length > 48)
        {
            baseId = baseId[..48].TrimEnd(' ', '.');
        }

        var candidate = baseId;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(VersionsRoot, candidate)) ||
               File.Exists(Path.Combine(VersionsRoot, candidate + ".json")))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private bool IsVanillaAssetIndexPresent(JsonObject version)
    {
        var assetId = version["assetIndex"]?["id"]?.GetValue<string>()
                      ?? version["assets"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return true;
        }

        return File.Exists(Path.Combine(_settings.Current.GameRoot, "assets", "indexes", assetId + ".json"));
    }

    private static void TouchBaseMarker(string versionDir)
    {
        var marker = Path.Combine(versionDir, ".mclneo-base");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "This folder is a shared vanilla base managed by MCLneo.");
        }
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        string stage,
        double fraction,
        string detail,
        int done = 0,
        int total = 0,
        long bytesDone = 0,
        long bytesTotal = 0)
    {
        progress?.Report(new InstallProgress(stage, Math.Clamp(fraction, 0, 1), detail,
            done, total, bytesDone, bytesTotal));
    }

    private async Task SaveMetadataAsync(GameInstance instance, CancellationToken ct)
    {
        var metadata = new InstanceMetadata
        {
            Name = instance.Name,
            MinecraftVersion = instance.MinecraftVersion,
            Loader = instance.Loader,
            LoaderVersion = instance.LoaderVersion,
            CreatedAt = instance.CreatedAt,
            LastPlayed = instance.LastPlayed
        };
        await PathSafety.WriteJsonAtomicAsync(
            Path.Combine(instance.Directory, "mclneo.instance.json"), metadata, ct).ConfigureAwait(false);
    }

    private sealed class ScaledProgress : IProgress<InstallProgress>
    {
        private readonly IProgress<InstallProgress>? _inner;
        private readonly double _start;
        private readonly double _end;

        public ScaledProgress(IProgress<InstallProgress>? inner, double start, double end)
        {
            _inner = inner;
            _start = start;
            _end = end;
        }

        public void Report(InstallProgress value)
        {
            _inner?.Report(value with
            {
                Fraction = _start + (_end - _start) * Math.Clamp(value.Fraction, 0, 1)
            });
        }
    }
}
