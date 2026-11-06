using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Minecraft;
using MCLneo.Core.Models;

namespace MCLneo.Core.Instances;

public sealed class InstanceService
{
    private static readonly Regex FabricVersionRegex =
        new(@"fabric-loader[:/-](?<v>[0-9][^:/]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly MinecraftInstaller _installer;
    private readonly AppLog _log;

    public InstanceService(AppPaths paths, SettingsService settings, MinecraftInstaller installer, AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _installer = installer;
        _log = log;
    }

    public string VersionsRoot => Path.Combine(_settings.Current.GameRoot, "versions");

    public async Task<IReadOnlyList<GameInstance>> ScanAsync(CancellationToken ct = default)
    {
        var result = new List<GameInstance>();
        if (!Directory.Exists(VersionsRoot))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(VersionsRoot))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(id) || File.Exists(Path.Combine(dir, ".mclneo-base")))
            {
                continue;
            }

            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                result.Add(await ReadInstanceAsync(dir, id, jsonPath, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _log.Warn($"读取实例失败：{dir}", ex);
            }
        }

        return result
            .OrderByDescending(i => i.LastPlayed)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<GameInstance?> GetAsync(string id, CancellationToken ct = default)
    {
        var all = await ScanAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<GameInstance> ReadInstanceAsync(string dir, string id, string jsonPath, CancellationToken ct)
    {
        var metadataPath = Path.Combine(dir, "mclneo.instance.json");
        InstanceMetadata? metadata = null;
        if (File.Exists(metadataPath))
        {
            try
            {
                metadata = await PathSafety.ReadJsonAsync<InstanceMetadata>(metadataPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn($"实例元数据损坏：{metadataPath}", ex);
            }
        }

        var json = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false)) as JsonObject
                   ?? throw new InvalidDataException("版本 JSON 为空");
        var inherited = json["inheritsFrom"]?.GetValue<string>();
        var loader = metadata?.Loader ?? InferLoader(json);
        var loaderVersion = metadata?.LoaderVersion ?? InferLoaderVersion(json, loader);
        var minecraftVersion = metadata?.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            minecraftVersion = inherited ?? id;
        }

        return new GameInstance
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(metadata?.Name) ? id : metadata.Name,
            Directory = dir,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            CreatedAt = metadata?.CreatedAt ?? Directory.GetCreationTime(dir),
            LastPlayed = metadata?.LastPlayed ?? File.GetLastWriteTime(dir),
            IsImported = metadata is null
        };
    }

    private static LoaderKind InferLoader(JsonObject json)
    {
        var mainClass = json["mainClass"]?.GetValue<string>() ?? "";
        if (mainClass.Contains("quilt", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderKind.Quilt;
        }

        if (mainClass.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderKind.Fabric;
        }

        if (mainClass.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderKind.NeoForge;
        }

        if (mainClass.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
            (json["libraries"] as JsonArray)?.Any(n =>
                n?["name"]?.GetValue<string>()?.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase) == true) == true)
        {
            return LoaderKind.Forge;
        }

        return LoaderKind.Vanilla;
    }

    private static string? InferLoaderVersion(JsonObject json, LoaderKind loader)
    {
        if (loader == LoaderKind.Vanilla || json["libraries"] is not JsonArray libraries)
        {
            return null;
        }

        foreach (var library in libraries.OfType<JsonObject>())
        {
            var name = library["name"]?.GetValue<string>() ?? "";
            if (loader == LoaderKind.Fabric && name.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase))
            {
                var match = FabricVersionRegex.Match(name);
                if (match.Success) return match.Groups["v"].Value;
            }

            if (loader == LoaderKind.Quilt && name.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase))
            {
                var parts = name.Split(':');
                if (parts.Length >= 3) return parts[2];
            }

            if (loader is LoaderKind.Forge or LoaderKind.NeoForge &&
                (name.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("neoforge", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = name.Split(':');
                if (parts.Length >= 3) return parts[2];
            }
        }

        return null;
    }

    public async Task SaveMetadataAsync(GameInstance instance, CancellationToken ct = default)
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

    public async Task MarkPlayedAsync(GameInstance instance, CancellationToken ct = default)
    {
        instance.LastPlayed = DateTimeOffset.Now;
        await SaveMetadataAsync(instance, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(GameInstance instance, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("实例名称不能为空", nameof(newName));
        }

        instance.Name = newName.Trim();
        await SaveMetadataAsync(instance, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(GameInstance instance, CancellationToken ct = default)
    {
        var target = Path.Combine(_paths.TrashDirectory,
            $"{instance.Id}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(_paths.TrashDirectory);
        await Task.Run(() => Directory.Move(instance.Directory, target), ct).ConfigureAwait(false);
        _log.Info($"实例已移入回收目录：{instance.Name} -> {target}");
    }

    public async Task<long> GetSizeAsync(GameInstance instance, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(instance.Directory));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    foreach (var file in dir.EnumerateFiles())
                    {
                        total += file.Length;
                    }

                    foreach (var sub in dir.EnumerateDirectories())
                    {
                        if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            stack.Push(sub);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return total;
        }, ct).ConfigureAwait(false);
    }

    public async Task<string> ExportAsync(
        GameInstance instance,
        string destinationPath,
        bool includeSaves = true,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        instance.EnsureDirectories();
        var files = new List<(string Path, string Entry)>();
        void AddFile(string path, string entry)
        {
            if (File.Exists(path))
            {
                files.Add((path, entry));
            }
        }

        AddFile(Path.Combine(instance.Directory, "mclneo.instance.json"), "mclneo.instance.json");
        AddFile(Path.Combine(instance.Directory, instance.Id + ".json"), instance.Id + ".json");
        AddFile(Path.Combine(instance.Directory, "options.txt"), "options.txt");
        AddFile(Path.Combine(instance.Directory, "servers.dat"), "servers.dat");
        AddDirectory(files, Path.Combine(instance.Directory, "mods"), "mods");
        AddDirectory(files, Path.Combine(instance.Directory, "config"), "config");
        AddDirectory(files, Path.Combine(instance.Directory, "resourcepacks"), "resourcepacks");
        AddDirectory(files, Path.Combine(instance.Directory, "shaderpacks"), "shaderpacks");
        if (includeSaves)
        {
            AddDirectory(files, Path.Combine(instance.Directory, "saves"), "saves");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using (var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false))
        {
            var manifestEntry = archive.CreateEntry("pack.json", CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(manifestStream, new
                {
                    format = "mclneo-instance",
                    version = 1,
                    instance = new
                    {
                        name = instance.Name,
                        minecraftVersion = instance.MinecraftVersion,
                        loader = instance.Loader.ToString(),
                        loaderVersion = instance.LoaderVersion,
                        exportedAt = DateTimeOffset.Now
                    }
                }, JsonDefaults.Options, ct).ConfigureAwait(false);
            }

            var done = 0;
            foreach (var (path, entryName) in files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var source = File.OpenRead(path);
                await source.CopyToAsync(entryStream, ct).ConfigureAwait(false);
                done++;
                progress?.Report(done / (double)files.Count);
            }
        }

        _log.Info($"导出实例：{instance.Name} -> {destinationPath}");
        return destinationPath;
    }

    public async Task<GameInstance> ImportAsync(
        string archivePath,
        string? newName = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(VersionsRoot);
        await using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("pack.json")
                            ?? throw new InvalidDataException("不是有效的 MCLneo 实例包：缺少 pack.json");
        JsonNode? manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonNode.ParseAsync(manifestStream, cancellationToken: ct).ConfigureAwait(false);
        }

        if (manifest?["format"]?.GetValue<string>() != "mclneo-instance")
        {
            throw new InvalidDataException("实例包格式不受支持");
        }

        var metadataName = manifest["instance"]?["name"]?.GetValue<string>() ?? "Imported";
        var displayName = string.IsNullOrWhiteSpace(newName) ? metadataName : newName.Trim();
        var baseId = PathSafety.SafeFileName(displayName, "Imported");
        var id = baseId;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(VersionsRoot, id)))
        {
            id = $"{baseId}-{suffix++}";
        }

        var targetDir = Path.Combine(VersionsRoot, id);
        Directory.CreateDirectory(targetDir);
        var extracted = 0;
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var destination = PathSafety.EnsureInside(targetDir, Path.Combine(targetDir, entry.FullName));
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                extracted++;
                progress?.Report(extracted / (double)entries.Count);
            }

            var jsonPath = Path.Combine(targetDir, id + ".json");
            if (!File.Exists(jsonPath))
            {
                var jsonEntry = entries.FirstOrDefault(e =>
                    e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !e.FullName.Contains("mclneo.instance", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.IndexOf('/') == e.FullName.LastIndexOf('/'));
                if (jsonEntry is null)
                {
                    throw new InvalidDataException("实例包中缺少版本 JSON");
                }

                File.Copy(Path.Combine(targetDir, jsonEntry.FullName), jsonPath);
            }

            var profile = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false)) as JsonObject
                          ?? throw new InvalidDataException("版本 JSON 无效");
            profile["id"] = id;
            await PathSafety.WriteJsonAtomicAsync(jsonPath, profile, ct).ConfigureAwait(false);

            var instance = new GameInstance
            {
                Id = id,
                Name = displayName,
                Directory = targetDir,
                MinecraftVersion = profile["inheritsFrom"]?.GetValue<string>() ??
                                   manifest["instance"]?["minecraftVersion"]?.GetValue<string>() ?? id,
                Loader = Enum.TryParse<LoaderKind>(manifest["instance"]?["loader"]?.GetValue<string>(), true, out var loader)
                    ? loader
                    : LoaderKind.Vanilla,
                LoaderVersion = manifest["instance"]?["loaderVersion"]?.GetValue<string>(),
                CreatedAt = DateTimeOffset.Now
            };
            if (!Directory.Exists(Path.Combine(VersionsRoot, instance.MinecraftVersion)) &&
                !string.IsNullOrWhiteSpace(profile["inheritsFrom"]?.GetValue<string>()))
            {
                throw new InvalidDataException(
                    $"缺少基础版本 Minecraft {instance.MinecraftVersion}，请先安装原版该版本后再导入。");
            }

            instance.EnsureDirectories();
            await SaveMetadataAsync(instance, ct).ConfigureAwait(false);
            _log.Info($"导入实例：{archivePath} -> {instance.Name}");
            return instance;
        }
        catch
        {
            try
            {
                Directory.Delete(targetDir, true);
            }
            catch
            {
            }

            throw;
        }
    }

    private static void AddDirectory(List<(string Path, string Entry)> files, string directory, string prefix)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            files.Add((file, Path.Combine(prefix, relative)));
        }
    }
}
