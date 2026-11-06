using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Minecraft;

public sealed record LoaderVersionInfo(string Version, bool Recommended);

/// <summary>Installs Forge/NeoForge by running the official installer headlessly.</summary>
public sealed class LoaderInstallerService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly VersionCatalog _catalog;
    private readonly HttpService _http;
    private readonly JavaService _java;
    private readonly AppLog _log;

    public LoaderInstallerService(
        AppPaths paths,
        SettingsService settings,
        DownloadService downloads,
        VersionCatalog catalog,
        HttpService http,
        JavaService java,
        AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _downloads = downloads;
        _catalog = catalog;
        _http = http;
        _java = java;
        _log = log;
    }

    public string VersionsRoot => Path.Combine(_settings.Current.GameRoot, "versions");

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken ct = default)
    {
        try
        {
            var text = await _http.GetStringAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("promos", out var promos))
            {
                return Array.Empty<LoaderVersionInfo>();
            }

            var latest = promos.TryGetProperty(minecraftVersion + "-latest", out var l) ? l.GetString() : null;
            var recommended = promos.TryGetProperty(minecraftVersion + "-recommended", out var r)
                ? r.GetString()
                : null;
            var result = new List<LoaderVersionInfo>();
            if (!string.IsNullOrWhiteSpace(recommended))
            {
                result.Add(new LoaderVersionInfo(recommended, true));
            }

            if (!string.IsNullOrWhiteSpace(latest) && latest != recommended)
            {
                result.Add(new LoaderVersionInfo(latest, false));
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Warn("获取 Forge 版本失败", ex);
            return Array.Empty<LoaderVersionInfo>();
        }
    }

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetNeoForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken ct = default)
    {
        try
        {
            var text = await _http.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct)
                .ConfigureAwait(false);
            var prefix = NeoForgePrefix(minecraftVersion);
            var versions = Regex.Matches(text, "<version>([^<]+)</version>")
                .Select(m => m.Groups[1].Value)
                .Where(v => v.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                .Where(v => !v.Contains("beta", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => ParseVersion(v))
                .Take(10)
                .Select((v, i) => new LoaderVersionInfo(v, i == 0))
                .ToList();
            return versions;
        }
        catch (Exception ex)
        {
            _log.Warn("获取 NeoForge 版本失败", ex);
            return Array.Empty<LoaderVersionInfo>();
        }
    }

    public static string NeoForgePrefix(string minecraftVersion)
    {
        if (minecraftVersion == "1.20.1")
        {
            return "47.1";
        }

        var parts = minecraftVersion.Split('.');
        return parts.Length >= 3 ? $"{parts[1]}.{parts[2]}" : minecraftVersion;
    }

    public async Task<GameInstance> InstallAsync(
        LoaderKind loader,
        MinecraftVersionInfo game,
        string instanceName,
        string? loaderVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (loader is not (LoaderKind.Forge or LoaderKind.NeoForge))
        {
            throw new ArgumentException("仅支持 Forge/NeoForge", nameof(loader));
        }

        var java = await _java.EnsureAsync(JavaService.RequiredMajor(game.Id), _settings.Current.JavaPath,
            new ScaledProgress(progress, 0.01, 0.08), ct).ConfigureAwait(false);
        if (loader == LoaderKind.Forge)
        {
            await EnsureVanillaAsync(game, progress, ct).ConfigureAwait(false);
        }
        else
        {
            await EnsureVanillaAsync(game, progress, ct).ConfigureAwait(false);
        }

        var version = string.IsNullOrWhiteSpace(loaderVersion)
            ? await ResolveLatestAsync(loader, game.Id, ct).ConfigureAwait(false)
            : loaderVersion.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"没有找到适用于 Minecraft {game.Id} 的 {loader} 版本");
        }

        Report(progress, $"准备 {loader}", 0.18, $"{loader} {version}");
        EnsureLauncherProfiles();
        var (installerUrl, fileName, installedVersionId) = BuildInstaller(loader, game.Id, version);
        var installerPath = Path.Combine(_paths.CacheDirectory, fileName);
        progress?.Report(new InstallProgress($"下载 {loader} 安装器", 0.2, fileName));
        await _downloads.DownloadFileAsync(new DownloadItem(
            installerUrl, installerPath, null, -1, fileName), null, ct).ConfigureAwait(false);

        var gameRoot = _settings.Current.GameRoot;
        var installLog = Path.Combine(_paths.LogsDirectory,
            $"{loader.ToString().ToLowerInvariant()}-install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Report(progress, $"运行 {loader} 安装器", 0.25, "安装器会下载运行库，可能需要几分钟");
        var exitCode = await RunInstallerAsync(java.ExecutablePath, installerPath, gameRoot,
            installLog, progress, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var tail = ReadTail(installLog, 30);
            throw new InvalidOperationException(
                $"{loader} 安装器退出代码 {exitCode}。日志：{installLog}\n{tail}");
        }

        var installedDir = Path.Combine(VersionsRoot, installedVersionId);
        if (!File.Exists(Path.Combine(installedDir, installedVersionId + ".json")))
        {
            // Some installer versions normalize the profile id; find the newest match.
            installedDir = Directory.EnumerateDirectories(VersionsRoot)
                .Where(d => Path.GetFileName(d).Contains(loader.ToString(), StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(d).Contains("forge", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTime)
                .FirstOrDefault() ?? installedDir;
        }

        var installedId = Path.GetFileName(installedDir);
        if (!File.Exists(Path.Combine(installedDir, installedId + ".json")))
        {
            throw new InvalidOperationException($"安装器没有生成 {loader} 版本。日志：{installLog}");
        }

        Report(progress, "创建实例", 0.9, "正在建立独立游戏目录");
        var instanceId = GetUniqueInstanceId(instanceName);
        var instanceDir = Path.Combine(VersionsRoot, instanceId);
        Directory.CreateDirectory(instanceDir);
        var profile = new JsonObject
        {
            ["id"] = instanceId,
            ["inheritsFrom"] = installedId,
            ["type"] = "release"
        };
        await PathSafety.WriteJsonAtomicAsync(
            Path.Combine(instanceDir, instanceId + ".json"), profile, ct).ConfigureAwait(false);
        TouchBase(installedDir);
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = string.IsNullOrWhiteSpace(instanceName) ? instanceId : instanceName.Trim(),
            Directory = instanceDir,
            MinecraftVersion = game.Id,
            Loader = loader,
            LoaderVersion = version,
            CreatedAt = DateTimeOffset.Now,
            LastPlayed = DateTimeOffset.Now
        };
        instance.EnsureDirectories();
        await PathSafety.WriteJsonAtomicAsync(Path.Combine(instanceDir, "mclneo.instance.json"),
            new InstanceMetadata
            {
                Name = instance.Name,
                MinecraftVersion = game.Id,
                Loader = loader,
                LoaderVersion = version,
                CreatedAt = instance.CreatedAt,
                LastPlayed = instance.LastPlayed
            }, ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("安装完成", 1, $"{instance.Name} 已准备好"));
        _log.Info($"{loader} 实例安装完成：{instance.Name}（{installedId}）");
        return instance;
    }

    private async Task EnsureVanillaAsync(
        MinecraftVersionInfo game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var versionDir = Path.Combine(VersionsRoot, game.Id);
        var json = Path.Combine(versionDir, game.Id + ".json");
        var jar = Path.Combine(versionDir, game.Id + ".jar");
        if (File.Exists(json) && File.Exists(jar))
        {
            return;
        }

        var installer = new MinecraftInstaller(_paths, _settings, _downloads, _catalog, _http, _log);
        await installer.EnsureVanillaAsync(game, _settings.Current.DownloadSource,
            new ScaledProgress(progress, 0.08, 0.18), ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveLatestAsync(LoaderKind loader, string mc, CancellationToken ct)
    {
        var versions = loader == LoaderKind.Forge
            ? await GetForgeVersionsAsync(mc, ct).ConfigureAwait(false)
            : await GetNeoForgeVersionsAsync(mc, ct).ConfigureAwait(false);
        return versions.FirstOrDefault(v => v.Recommended)?.Version ?? versions.FirstOrDefault()?.Version;
    }

    private (string Url, string FileName, string InstalledVersionId) BuildInstaller(
        LoaderKind loader, string mc, string version)
    {
        if (loader == LoaderKind.Forge)
        {
            var id = $"{mc}-forge-{version}";
            return ($"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{version}/" +
                    $"forge-{mc}-{version}-installer.jar",
                $"forge-{mc}-{version}-installer.jar", id);
        }

        return ($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/" +
                $"neoforge-{version}-installer.jar",
            $"neoforge-{version}-installer.jar", $"neoforge-{version}");
    }

    private void EnsureLauncherProfiles()
    {
        var path = Path.Combine(_settings.Current.GameRoot, "launcher_profiles.json");
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(_settings.Current.GameRoot);
        File.WriteAllText(path,
            """{"profiles":{},"settings":{"enableSnapshots":false,"enableAdvanced":false,"keepLauncherOpen":false,"showMenu":false,"locale":"zh_CN"},"version":3}""",
            new UTF8Encoding(false));
    }

    private async Task<int> RunInstallerAsync(
        string java, string installer, string gameRoot, string logPath,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var psi = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installer) ?? _settings.Current.GameRoot,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(gameRoot);
        psi.Environment["TMP"] = _paths.TempDirectory;
        psi.Environment["TEMP"] = _paths.TempDirectory;
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动加载器安装器");
        var writer = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync($"=== {Path.GetFileName(installer)} ===").ConfigureAwait(false);
        var lastReport = Stopwatch.StartNew();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try { writer.WriteLine(e.Data); } catch { }
            if (lastReport.ElapsedMilliseconds > 400)
            {
                lastReport.Restart();
                var line = e.Data.Trim();
                if (line.Length > 0)
                {
                    progress?.Report(new InstallProgress("安装加载器", 0.3, line.Length > 100 ? line[..100] : line));
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try { writer.WriteLine(e.Data); } catch { }
        };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        finally
        {
            try { writer.Dispose(); } catch { }
        }

        return process.ExitCode;
    }

    private string GetUniqueInstanceId(string requested)
    {
        var baseId = PathSafety.SafeFileName(requested, "New-Instance");
        var candidate = baseId;
        var i = 2;
        while (Directory.Exists(Path.Combine(VersionsRoot, candidate)))
        {
            candidate = $"{baseId}-{i++}";
        }

        return candidate;
    }

    private static void TouchBase(string dir)
    {
        var marker = Path.Combine(dir, ".mclneo-base");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "Managed by MCLneo");
        }
    }

    private static string ReadTail(string path, int lines)
    {
        try
        {
            return string.Join(Environment.NewLine, File.ReadAllLines(path).TakeLast(lines));
        }
        catch
        {
            return "";
        }
    }

    private static Version ParseVersion(string text)
    {
        var clean = Regex.Replace(text, @"[^0-9.].*$", "");
        return Version.TryParse(clean, out var version) ? version : new Version(0, 0);
    }

    private static void Report(IProgress<InstallProgress>? progress, string stage, double fraction, string detail) =>
        progress?.Report(new InstallProgress(stage, Math.Clamp(fraction, 0, 1), detail));

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

        public void Report(InstallProgress value) =>
            _inner?.Report(value with { Fraction = _start + (_end - _start) * Math.Clamp(value.Fraction, 0, 1) });
    }
}
