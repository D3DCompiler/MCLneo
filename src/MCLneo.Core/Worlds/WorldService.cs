using System.IO.Compression;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Worlds;

public sealed class WorldInfo
{
    public required string FolderName { get; init; }
    public required string Directory { get; init; }
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public long LastPlayed { get; set; }
    public int GameType { get; set; }
    public bool Hardcore { get; set; }
    public string? IconPath { get; set; }
    public long SizeBytes { get; set; }

    public string GameModeText => GameType switch
    {
        1 => "创造模式",
        2 => "冒险模式",
        3 => "旁观模式",
        _ => Hardcore ? "极限模式" : "生存模式"
    };

    public string LastPlayedText
    {
        get
        {
            if (LastPlayed <= 0 || LastPlayed > 253402300799999)
            {
                return "未知";
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(LastPlayed).LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "未知";
            }
        }
    }

    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{SizeBytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{SizeBytes / 1024d:0.#} KB",
        _ => $"{SizeBytes} B"
    };
}

public sealed class WorldService
{
    private readonly AppPaths _paths;
    private readonly AppLog _log;

    public WorldService(AppPaths paths, AppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<IReadOnlyList<WorldInfo>> GetWorldsAsync(GameInstance instance, CancellationToken ct = default)
    {
        var saves = instance.SavesDirectory;
        if (!Directory.Exists(saves))
        {
            return Array.Empty<WorldInfo>();
        }

        var result = new List<WorldInfo>();
        foreach (var dir in Directory.EnumerateDirectories(saves))
        {
            ct.ThrowIfCancellationRequested();
            var levelDat = Path.Combine(dir, "level.dat");
            if (!File.Exists(levelDat))
            {
                continue;
            }

            try
            {
                var data = ReadLevelData(levelDat);
                var world = new WorldInfo
                {
                    FolderName = Path.GetFileName(dir),
                    Directory = dir,
                    Name = data.Name,
                    Version = data.Version,
                    LastPlayed = data.LastPlayed,
                    GameType = data.GameType,
                    Hardcore = data.Hardcore,
                    IconPath = File.Exists(Path.Combine(dir, "icon.png")) ? Path.Combine(dir, "icon.png") : null
                };
                world.SizeBytes = await Task.Run(() => GetDirectorySize(dir, ct), ct).ConfigureAwait(false);
                result.Add(world);
            }
            catch (Exception ex)
            {
                _log.Warn($"读取存档失败：{dir}", ex);
            }
        }

        return result.OrderByDescending(w => w.LastPlayed).ThenBy(w => w.Name).ToList();
    }

    public async Task<string> ExportAsync(
        WorldInfo world,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"目标文件已存在：{destinationPath}");
        }

        var files = Directory.EnumerateFiles(world.Directory, "*", SearchOption.AllDirectories).ToList();
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, false);
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(world.Directory, file).Replace('\\', '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var input = File.OpenRead(file);
            await input.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            done++;
            progress?.Report(done / (double)files.Count);
        }

        _log.Info($"导出存档：{world.Name} -> {destinationPath}");
        return destinationPath;
    }

    public async Task<string> BackupAsync(
        GameInstance instance,
        WorldInfo world,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var directory = Path.Combine(instance.Directory, "backups", "worlds");
        Directory.CreateDirectory(directory);
        var name = PathSafety.SafeFileName(world.Name, "world");
        var path = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
        await ExportAsync(world, path, progress, ct).ConfigureAwait(false);
        return path;
    }

    public IReadOnlyList<string> ListBackups(GameInstance instance)
    {
        var directory = Path.Combine(instance.Directory, "backups", "worlds");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.zip").OrderByDescending(File.GetLastWriteTime).ToList()
            : Array.Empty<string>();
    }

    public async Task<WorldInfo> ImportAsync(
        string archivePath,
        GameInstance instance,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(instance.SavesDirectory);
        var temp = Path.Combine(_paths.TempDirectory, "world-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await using (var stream = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                long total = 0;
                var done = 0;
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    total += entry.Length;
                    if (total > 8L * 1024 * 1024 * 1024)
                    {
                        throw new InvalidDataException("存档包过大（超过 8 GB）");
                    }

                    var target = PathSafety.EnsureInside(temp, Path.Combine(temp, entry.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(target, FileMode.Create, FileAccess.Write,
                        FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    done++;
                    progress?.Report(done / (double)entries.Count * 0.8);
                }
            }

            var root = FindWorldRoot(temp);
            var levelDat = Path.Combine(root, "level.dat");
            if (!File.Exists(levelDat))
            {
                throw new InvalidDataException("压缩包中找不到 level.dat，不是有效的 Minecraft 存档");
            }

            var data = ReadLevelData(levelDat);
            var baseName = PathSafety.SafeFileName(data.Name, "Imported World");
            var destination = Path.Combine(instance.SavesDirectory, baseName);
            var suffix = 2;
            while (Directory.Exists(destination))
            {
                destination = Path.Combine(instance.SavesDirectory, $"{baseName} ({suffix++})");
            }

            CopyDirectory(root, destination, ct);
            progress?.Report(1);
            _log.Info($"导入存档：{archivePath} -> {destination}");
            return new WorldInfo
            {
                FolderName = Path.GetFileName(destination),
                Directory = destination,
                Name = data.Name,
                Version = data.Version,
                LastPlayed = data.LastPlayed,
                GameType = data.GameType,
                Hardcore = data.Hardcore,
                IconPath = File.Exists(Path.Combine(destination, "icon.png"))
                    ? Path.Combine(destination, "icon.png")
                    : null,
                SizeBytes = GetDirectorySize(destination, ct)
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }
            }
            catch
            {
            }
        }
    }

    public async Task DeleteAsync(WorldInfo world, CancellationToken ct = default)
    {
        var target = Path.Combine(_paths.TrashDirectory,
            $"world-{Path.GetFileName(world.Directory)}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(_paths.TrashDirectory);
        await Task.Run(() => Directory.Move(world.Directory, target), ct).ConfigureAwait(false);
        _log.Info($"存档已移入回收目录：{world.Name} -> {target}");
    }

    public static string? CreateBackupName(WorldInfo world)
    {
        return $"{PathSafety.SafeFileName(world.Name, "world")}-{DateTime.Now:yyyyMMdd-HHmm}.zip";
    }

    private static string FindWorldRoot(string temp)
    {
        if (File.Exists(Path.Combine(temp, "level.dat")))
        {
            return temp;
        }

        var candidates = Directory.EnumerateDirectories(temp, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "level.dat")))
            .OrderBy(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .ToList();
        return candidates.FirstOrDefault() ?? temp;
    }

    private static LevelData ReadLevelData(string path)
    {
        var nbt = NbtReader.ReadGzip(path);
        var data = nbt.TryGetValue("Data", out var node) && node is Dictionary<string, object?> compound
            ? compound
            : nbt;

        return new LevelData(
            GetString(data, "LevelName") ?? "未命名世界",
            GetString(data, "Version", "Name"),
            GetLong(data, "LastPlayed"),
            GetInt(data, "GameType"),
            GetBool(data, "Hardcore"));
    }

    private static string? GetString(Dictionary<string, object?> data, params string[] path)
    {
        var current = data;
        for (var i = 0; i < path.Length; i++)
        {
            if (!current.TryGetValue(path[i], out var value))
            {
                return null;
            }

            if (i == path.Length - 1)
            {
                return value as string;
            }

            current = value as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        }

        return null;
    }

    private static long GetLong(Dictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) && value is long l ? l : 0;

    private static int GetInt(Dictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) && value is int i ? i : 0;

    private static bool GetBool(Dictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) && value is sbyte b && b != 0;

    private static long GetDirectorySize(string directory, CancellationToken ct)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                total += new FileInfo(file).Length;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = PathSafety.EnsureInside(destination, Path.Combine(destination, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private sealed record LevelData(string Name, string? Version, long LastPlayed, int GameType, bool Hardcore);
}
