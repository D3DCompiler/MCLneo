using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Mods;

public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = new();
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("display_categories")] public List<string> DisplayCategories { get; set; } = new();
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }

    public string DownloadText => Downloads switch
    {
        >= 10_000_000 => $"{Downloads / 10_000_000d:0.#}千万",
        >= 10_000 => $"{Downloads / 10_000d:0.#}万",
        _ => Downloads.ToString("N0")
    };
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("hashes")] public ModrinthHashes Hashes { get; set; } = new();
}

public sealed class ModrinthHashes
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
}

public sealed class InstalledMod
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string DisplayName { get; set; } = "";
    public string? Version { get; set; }
    public bool Enabled { get; set; }
    public long Size { get; set; }
    public string? ProjectType { get; set; }
    public string StatusText => Enabled ? "已启用" : "已禁用";
}

public sealed record ModInstallResult(IReadOnlyList<string> InstalledFiles, IReadOnlyList<string> Warnings);

public sealed class ModrinthService
{
    public const string BaseUrl = "https://api.modrinth.com/v2";
    private readonly HttpService _http;
    private readonly DownloadService _downloads;
    private readonly AppLog _log;

    public ModrinthService(HttpService http, DownloadService downloads, AppLog log)
    {
        _http = http;
        _downloads = downloads;
        _log = log;
    }

    public async Task<ModrinthSearchResponse> SearchAsync(
        string query,
        string projectType,
        string? gameVersion,
        string? loader,
        int offset = 0,
        int limit = 20,
        string index = "relevance",
        CancellationToken ct = default)
    {
        var facets = BuildFacets(projectType, gameVersion, loader);
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query ?? "")}" +
                  $"&limit={Math.Clamp(limit, 1, 50)}&offset={Math.Max(0, offset)}" +
                  $"&index={Uri.EscapeDataString(index)}" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        return await _http.GetJsonAsync<ModrinthSearchResponse>(url, ct).ConfigureAwait(false)
               ?? throw new InvalidDataException("Modrinth 返回了空搜索响应");
    }

    public static List<List<string>> BuildFacets(string projectType, string? gameVersion, string? loader)
    {
        var facets = new List<List<string>> { new() { "project_type:" + projectType } };
        if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "全部")
        {
            facets.Add(new List<string> { "versions:" + gameVersion });
        }

        if (!string.IsNullOrWhiteSpace(loader) && loader != "vanilla" && loader != "原版")
        {
            facets.Add(new List<string> { "categories:" + loader.ToLowerInvariant() });
        }

        return facets;
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        var versions = await _http.GetJsonAsync<List<ModrinthVersion>>(url, ct).ConfigureAwait(false)
                       ?? new List<ModrinthVersion>();
        return versions
            .Where(v => string.IsNullOrWhiteSpace(gameVersion) || v.GameVersions.Contains(gameVersion))
            .Where(v => string.IsNullOrWhiteSpace(loader) || loader == "vanilla" ||
                        v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(v => v.DatePublished)
            .ToList();
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        return await _http.GetJsonAsync<ModrinthVersion>(
            $"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}", ct).ConfigureAwait(false);
    }

    public async Task<ModInstallResult> InstallAsync(
        ModrinthProject project,
        GameInstance instance,
        string projectType,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var loader = LoaderId(instance.Loader);
        status?.Report($"正在查找兼容 {project.Title} 的版本…");
        var versions = await GetVersionsAsync(project.ProjectId, instance.MinecraftVersion, loader, ct)
            .ConfigureAwait(false);
        var version = versions.FirstOrDefault()
                      ?? throw new InvalidOperationException(
                          $"{project.Title} 没有适用于 Minecraft {instance.MinecraftVersion} / {instance.DisplayLoader} 的版本");
        var installed = new List<string>();
        var warnings = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await InstallVersionAsync(version, instance, projectType, installed, warnings, visiting, 0, status, ct)
            .ConfigureAwait(false);
        return new ModInstallResult(installed, warnings);
    }

    private async Task InstallVersionAsync(
        ModrinthVersion version,
        GameInstance instance,
        string projectType,
        List<string> installed,
        List<string> warnings,
        HashSet<string> visiting,
        int depth,
        IProgress<string>? status,
        CancellationToken ct)
    {
        if (depth > 5 || !visiting.Add(version.Id))
        {
            return;
        }

        try
        {
            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
            if (file is null || string.IsNullOrWhiteSpace(file.Url))
            {
                throw new InvalidDataException($"{version.Name} 没有可下载的文件");
            }

            var directory = TargetDirectory(instance, projectType);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, PathSafety.SafeFileName(file.Filename, "download.jar"));
            status?.Report($"下载 {version.Name}…");
            await _downloads.DownloadFileAsync(new DownloadItem(
                file.Url,
                destination,
                file.Hashes.Sha1,
                file.Size,
                file.Filename), null, ct).ConfigureAwait(false);
            installed.Add(destination);
            _log.Info($"已安装资源：{version.Name} -> {destination}");

            foreach (var dependency in version.Dependencies.Where(d => d.DependencyType == "required"))
            {
                if (string.IsNullOrWhiteSpace(dependency.VersionId) && string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    continue;
                }

                ModrinthVersion? depVersion = null;
                if (!string.IsNullOrWhiteSpace(dependency.VersionId))
                {
                    depVersion = await GetVersionAsync(dependency.VersionId, ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    var candidates = await GetVersionsAsync(dependency.ProjectId, instance.MinecraftVersion,
                        LoaderId(instance.Loader), ct).ConfigureAwait(false);
                    depVersion = candidates.FirstOrDefault();
                }

                if (depVersion is null)
                {
                    var message = $"依赖 {dependency.ProjectId ?? dependency.VersionId} 没有兼容版本";
                    warnings.Add(message);
                    _log.Warn(message);
                    continue;
                }

                await InstallVersionAsync(depVersion, instance, projectType, installed, warnings, visiting,
                    depth + 1, status, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            visiting.Remove(version.Id);
        }
    }

    public async Task<IReadOnlyList<InstalledMod>> GetInstalledAsync(
        GameInstance instance,
        string projectType = "mod",
        CancellationToken ct = default)
    {
        var directory = TargetDirectory(instance, projectType);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<InstalledMod>();
        }

        var result = new List<InstalledMod>();
        foreach (var path in Directory.EnumerateFiles(directory).Where(IsResourceFile))
        {
            ct.ThrowIfCancellationRequested();
            var enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var displayPath = enabled ? path : path[..^".disabled".Length];
            var info = new InstalledMod
            {
                FilePath = path,
                Enabled = enabled,
                Size = new FileInfo(path).Length,
                ProjectType = projectType,
                DisplayName = Path.GetFileName(displayPath),
                Version = null
            };
            try
            {
                var metadata = ReadMetadata(displayPath, projectType);
                if (!string.IsNullOrWhiteSpace(metadata.Name)) info.DisplayName = metadata.Name;
                info.Version = metadata.Version;
            }
            catch
            {
            }

            result.Add(info);
        }

        return result.OrderByDescending(m => m.Enabled).ThenBy(m => m.DisplayName).ToList();
    }

    public static string TargetDirectory(GameInstance instance, string projectType) => projectType switch
    {
        "resourcepack" => instance.ResourcePacksDirectory,
        "shader" => instance.ShaderPacksDirectory,
        "datapack" => Path.Combine(instance.SavesDirectory),
        "modpack" => throw new NotSupportedException("整合包安装将在后续里程碑提供"),
        _ => instance.ModsDirectory
    };

    public static void SetEnabled(InstalledMod mod, bool enabled)
    {
        if (mod.Enabled == enabled)
        {
            return;
        }

        if (enabled)
        {
            var target = mod.FilePath[..^".disabled".Length];
            File.Move(mod.FilePath, target, true);
        }
        else
        {
            File.Move(mod.FilePath, mod.FilePath + ".disabled", true);
        }
    }

    public static void Delete(InstalledMod mod)
    {
        if (File.Exists(mod.FilePath))
        {
            File.Delete(mod.FilePath);
        }
    }

    private static bool IsResourceFile(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.EndsWith(".jar") || lower.EndsWith(".zip") ||
               lower.EndsWith(".jar.disabled") || lower.EndsWith(".zip.disabled");
    }

    private static (string? Name, string? Version) ReadMetadata(string path, string projectType)
    {
        if (projectType != "mod" || !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        using var archive = ZipFile.OpenRead(path);
        var fabric = archive.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            using var stream = fabric.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return (
                root.TryGetProperty("name", out var name) ? name.GetString() : null,
                root.TryGetProperty("version", out var version) ? version.GetString() : null);
        }

        var quilt = archive.GetEntry("quilt.mod.json");
        if (quilt is not null)
        {
            using var stream = quilt.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("quilt_loader", out var loader))
            {
                return (
                    loader.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("name", out var name)
                        ? name.GetString() : null,
                    loader.TryGetProperty("version", out var version) ? version.GetString() : null);
            }
        }

        var toml = archive.GetEntry("META-INF/mods.toml") ?? archive.GetEntry("META-INF/neoforge.mods.toml");
        if (toml is not null)
        {
            using var reader = new StreamReader(toml.Open());
            var text = reader.ReadToEnd();
            var display = Regex.Match(text, @"displayName\s*=\s*""(?<v>[^""]+)""");
            var modId = Regex.Match(text, @"modId\s*=\s*""(?<v>[^""]+)""");
            var version = Regex.Match(text, @"version\s*=\s*""(?<v>[^""]+)""");
            return (
                display.Success ? display.Groups["v"].Value : modId.Success ? modId.Groups["v"].Value : null,
                version.Success ? version.Groups["v"].Value : null);
        }

        return (null, null);
    }

    public static string LoaderId(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => "vanilla"
    };
}
