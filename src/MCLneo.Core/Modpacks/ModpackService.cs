using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;
using MCLneo.Core.Mods;
using MCLneo.Core.Minecraft;

namespace MCLneo.Core.Modpacks;

/// <summary>Installs Modrinth .mrpack modpacks into a new isolated instance.</summary>
public sealed class ModpackService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly VersionCatalog _catalog;
    private readonly MinecraftInstaller _installer;
    private readonly LoaderInstallerService _loaderInstaller;
    private readonly ModrinthService _modrinth;
    private readonly AppLog _log;

    public ModpackService(
        AppPaths paths,
        SettingsService settings,
        DownloadService downloads,
        VersionCatalog catalog,
        MinecraftInstaller installer,
        LoaderInstallerService loaderInstaller,
        ModrinthService modrinth,
        AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _downloads = downloads;
        _catalog = catalog;
        _installer = installer;
        _loaderInstaller = loaderInstaller;
        _modrinth = modrinth;
        _log = log;
    }

    public async Task<GameInstance> InstallAsync(
        ModrinthProject project,
        string instanceName,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var versions = await _modrinth.GetVersionsAsync(project.ProjectId, ct: ct).ConfigureAwait(false);
        var version = versions.FirstOrDefault()
                      ?? throw new InvalidOperationException($"整合包 {project.Title} 没有可用版本");
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("整合包文件为空");
        if (!file.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("目前只支持 Modrinth .mrpack 整合包");
        }

        var archive = Path.Combine(_paths.CacheDirectory, PathSafety.SafeFileName(file.Filename, "pack.mrpack"));
        progress?.Report(new InstallProgress("下载整合包", 0.03, file.Filename));
        await _downloads.DownloadFileAsync(new DownloadItem(
            file.Url, archive, file.Hashes.Sha1, file.Size, file.Filename), null, ct).ConfigureAwait(false);
        return await InstallArchiveAsync(archive, project.Title, instanceName, progress, ct,
            project.ProjectId, version.Id, version.VersionNumber).ConfigureAwait(false);
    }

    public async Task<GameInstance> InstallArchiveAsync(
        string archivePath,
        string packTitle,
        string instanceName,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? projectId = null,
        string? versionId = null,
        string? versionNumber = null)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var indexEntry = zip.GetEntry("modrinth.index.json")
                         ?? throw new InvalidDataException("整合包缺少 modrinth.index.json");
        JsonNode index;
        await using (var stream = indexEntry.Open())
        {
            index = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException("modrinth.index.json 无效");
        }

        var dependencies = index["dependencies"] as JsonObject
                           ?? throw new InvalidDataException("整合包缺少 dependencies");
        var mcVersion = dependencies["minecraft"]?.GetValue<string>()
                        ?? throw new InvalidDataException("整合包未指定 Minecraft 版本");
        var loader = ReadLoader(dependencies, out var loaderVersion);
        progress?.Report(new InstallProgress("准备整合包游戏", 0.1, $"Minecraft {mcVersion} · {loader}"));

        var manifest = await _catalog.GetManifestAsync(_settings.Current.DownloadSource, ct: ct)
            .ConfigureAwait(false);
        var game = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                   ?? throw new InvalidOperationException($"版本清单中没有 Minecraft {mcVersion}");

        GameInstance instance;
        if (loader is LoaderKind.Forge or LoaderKind.NeoForge)
        {
            instance = await _loaderInstaller.InstallAsync(loader, game, instanceName, loaderVersion,
                Scale(progress, 0.12, 0.7), ct).ConfigureAwait(false);
        }
        else
        {
            instance = await _installer.InstallAsync(
                new InstallRequest(game, instanceName, loader, loaderVersion),
                Scale(progress, 0.12, 0.7), ct).ConfigureAwait(false);
        }

        var files = index["files"] as JsonArray ?? new JsonArray();
        var downloads = new List<DownloadItem>();
        foreach (var node in files.OfType<JsonObject>())
        {
            var relative = node["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var destination = PathSafety.EnsureInside(instance.Directory,
                Path.Combine(instance.Directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            var urls = node["downloads"] as JsonArray;
            var url = urls?.Select(u => u?.GetValue<string>()).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var sha1 = node["hashes"]?["sha1"]?.GetValue<string>();
            var size = node["fileSize"]?.GetValue<long>() ?? -1;
            downloads.Add(new DownloadItem(url, destination, sha1, size, Path.GetFileName(relative)));
        }

        progress?.Report(new InstallProgress("下载整合包内容", 0.72, $"共 {downloads.Count} 个文件"));
        await _downloads.DownloadBatchAsync(downloads, null,
            _settings.Current.MaxDownloadThreads, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("应用整合包覆盖文件", 0.9, "overrides"));
        ExtractOverrides(zip, "overrides", instance.Directory, ct);
        ExtractOverrides(zip, "client-overrides", instance.Directory, ct);

        instance.Name = packTitle;
        instance.EnsureDirectories();
        await PathSafety.WriteJsonAtomicAsync(Path.Combine(instance.Directory, "mclneo.instance.json"),
            new InstanceMetadata
            {
                Name = instance.Name,
                MinecraftVersion = mcVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                CreatedAt = instance.CreatedAt,
                LastPlayed = DateTimeOffset.Now
            }, ct).ConfigureAwait(false);
        await PathSafety.WriteJsonAtomicAsync(Path.Combine(instance.Directory, "mclneo.modpack.json"),
            new
            {
                projectId,
                projectTitle = packTitle,
                versionId,
                versionNumber,
                installedAt = DateTimeOffset.Now
            }, ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("整合包安装完成", 1, instance.Name));
        _log.Info($"整合包安装完成：{packTitle} -> {instance.Directory}");
        return instance;
    }

    private static LoaderKind ReadLoader(JsonObject dependencies, out string? version)
    {
        version = null;
        if (dependencies["fabric-loader"] is JsonNode fabric)
        {
            version = fabric.GetValue<string>();
            return LoaderKind.Fabric;
        }

        if (dependencies["quilt-loader"] is JsonNode quilt)
        {
            version = quilt.GetValue<string>();
            return LoaderKind.Quilt;
        }

        if (dependencies["neoforge"] is JsonNode neo)
        {
            version = neo.GetValue<string>();
            return LoaderKind.NeoForge;
        }

        if (dependencies["forge"] is JsonNode forge)
        {
            version = forge.GetValue<string>();
            return LoaderKind.Forge;
        }

        return LoaderKind.Vanilla;
    }

    private static void ExtractOverrides(ZipArchive zip, string prefix, string targetRoot, CancellationToken ct)
    {
        foreach (var entry in zip.Entries.Where(e =>
                     e.FullName.Replace('\\', '/').StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(e.Name)))
        {
            ct.ThrowIfCancellationRequested();
            var full = entry.FullName.Replace('\\', '/');
            var relative = full[(prefix.Length + 1)..];
            var target = PathSafety.EnsureInside(targetRoot,
                Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static IProgress<InstallProgress>? Scale(
        IProgress<InstallProgress>? inner, double start, double end) =>
        inner is null ? null : new ScaledProgress(inner, start, end);

    private sealed class ScaledProgress : IProgress<InstallProgress>
    {
        private readonly IProgress<InstallProgress> _inner;
        private readonly double _start;
        private readonly double _end;

        public ScaledProgress(IProgress<InstallProgress> inner, double start, double end)
        {
            _inner = inner;
            _start = start;
            _end = end;
        }

        public void Report(InstallProgress value) =>
            _inner.Report(value with { Fraction = _start + (_end - _start) * Math.Clamp(value.Fraction, 0, 1) });
    }
}
