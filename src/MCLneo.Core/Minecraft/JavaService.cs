using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Minecraft;

public sealed record JavaRuntime(
    string ExecutablePath,
    string Version,
    int Major,
    string Vendor,
    string Source)
{
    public string Display => $"Java {Major} · {Vendor}";
}

public sealed class JavaService
{
    private static readonly Regex VersionRegex = new("version \"(?<v>[^\"]+)\"", RegexOptions.Compiled);
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly DownloadService _downloads;
    private readonly AppLog _log;

    public JavaService(AppPaths paths, HttpService http, DownloadService downloads, AppLog log)
    {
        _paths = paths;
        _http = http;
        _downloads = downloads;
        _log = log;
    }

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(
        string? preferredPath = null,
        CancellationToken ct = default)
    {
        var candidates = new List<(string Path, string Source)>();
        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (Directory.Exists(path))
                {
                    path = Path.Combine(path, "bin", "java.exe");
                }

                if (File.Exists(path))
                {
                    candidates.Add((Path.GetFullPath(path), source));
                }
            }
            catch
            {
            }
        }

        Add(preferredPath, "设置");
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(Path.Combine(dir, "java.exe"), "PATH");
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
            _paths.RuntimeDirectory
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var java in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories)
                             .Take(24))
                {
                    Add(java, "自动扫描");
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var results = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, source) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(path))
            {
                continue;
            }

            var runtime = await InspectAsync(path, source, ct).ConfigureAwait(false);
            if (runtime is not null)
            {
                results.Add(runtime);
            }
        }

        return results
            .OrderByDescending(r => r.Major)
            .ThenBy(r => r.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<JavaRuntime> EnsureAsync(
        int requiredMajor,
        string? preferredPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new InstallProgress("检查 Java", 0.02, $"需要 Java {requiredMajor}"));
        var installed = await DiscoverAsync(preferredPath, ct).ConfigureAwait(false);
        var selected = Select(installed, requiredMajor, preferredPath);
        if (selected is not null)
        {
            _log.Info($"使用 Java：{selected.ExecutablePath}（{selected.Version}）");
            return selected;
        }

        progress?.Report(new InstallProgress("准备 Java", 0.05, $"正在获取 Java {requiredMajor}"));
        var runtime = await InstallAdoptiumAsync(requiredMajor, progress, ct).ConfigureAwait(false);
        _log.Info($"已安装 Java {runtime.Major}：{runtime.ExecutablePath}");
        return runtime;
    }

    public Task<JavaRuntime> InstallRuntimeAsync(
        int major,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default) =>
        InstallAdoptiumAsync(Math.Clamp(major, 8, 25), progress, ct);

    public bool IsManagedRuntime(JavaRuntime runtime)
    {
        var path = Path.GetFullPath(runtime.ExecutablePath);
        var root = Path.GetFullPath(_paths.RuntimeDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public Task DeleteRuntimeAsync(JavaRuntime runtime, CancellationToken ct = default)
    {
        if (!IsManagedRuntime(runtime))
        {
            throw new InvalidOperationException("只能删除由 MCLneo 下载的 Java 运行时");
        }

        var root = Path.GetFullPath(_paths.RuntimeDirectory);
        var directory = Path.GetDirectoryName(Path.GetFullPath(runtime.ExecutablePath))!;
        while (Directory.GetParent(directory) is { } parent &&
               !string.Equals(parent.FullName.TrimEnd(Path.DirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            directory = parent.FullName;
        }

        return Task.Run(() =>
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }, ct);
    }

    public static JavaRuntime? Select(
        IReadOnlyList<JavaRuntime> runtimes,
        int requiredMajor,
        string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var preferred = runtimes.FirstOrDefault(r =>
                string.Equals(r.ExecutablePath, Path.GetFullPath(preferredPath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(r.ExecutablePath)),
                    Path.GetFullPath(preferredPath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
            if (preferred is not null && preferred.Major >= requiredMajor)
            {
                return preferred;
            }
        }

        var exact = runtimes.FirstOrDefault(r => r.Major == requiredMajor);
        if (exact is not null)
        {
            return exact;
        }

        return runtimes
            .Where(r => r.Major >= requiredMajor)
            .OrderBy(r => r.Major)
            .FirstOrDefault();
    }

    public static int RequiredMajor(string minecraftVersion)
    {
        var text = minecraftVersion.Trim();
        if (text.StartsWith("1.", StringComparison.Ordinal) &&
            Version.TryParse(text.Length > 2 ? text : text + ".0", out var v))
        {
            var minor = v.Minor;
            var patch = v.Build;
            if (minor > 20 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }

        var snapshotYear = Regex.Match(text, @"^(?<y>\d{2})w");
        if (snapshotYear.Success && int.TryParse(snapshotYear.Groups["y"].Value, out var year) && year >= 24)
        {
            return 21;
        }

        return 17;
    }

    public async Task<JavaRuntime?> InspectAsync(string path, string source = "手动", CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            var output = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
            var match = VersionRegex.Match(output);
            if (!match.Success)
            {
                return null;
            }

            var version = match.Groups["v"].Value;
            var major = ParseMajor(version);
            if (major <= 0)
            {
                return null;
            }

            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Java";
            var vendor = firstLine.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ? "Eclipse Temurin"
                : firstLine.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Microsoft"
                : firstLine.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase) ? "OpenJDK"
                : firstLine.Contains("Corretto", StringComparison.OrdinalIgnoreCase) ? "Amazon Corretto"
                : "Java";
            return new JavaRuntime(path, version, major, vendor, source);
        }
        catch (Exception ex)
        {
            _log.Debug($"检测 Java 失败：{path}：{ex.Message}");
            return null;
        }
    }

    private static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (parts.Length >= 2 && parts[0] == "1" && int.TryParse(parts[1], out var legacy))
        {
            return legacy;
        }

        return int.TryParse(parts[0], out var major) ? major : 0;
    }

    private async Task<JavaRuntime> InstallAdoptiumAsync(
        int major,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                  $"?architecture={arch}&image_type=jre&os=windows&vendor=eclipse";
        progress?.Report(new InstallProgress("查询 Java", 0.08, "正在查询 Eclipse Adoptium"));
        using var document = await _http.GetJsonAsync(url, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Adoptium 没有适用于 Java {major} 的 Windows JRE");
        }

        var binary = root[0].GetProperty("binary");
        var package = binary.GetProperty("package");
        var link = package.GetProperty("link").GetString()
                   ?? throw new InvalidDataException("Java 下载地址为空");
        var fileName = package.GetProperty("name").GetString() ?? $"java-{major}.zip";
        var size = package.TryGetProperty("size", out var s) ? s.GetInt64() : -1;

        var archive = Path.Combine(_paths.CacheDirectory, fileName);
        await _downloads.DownloadFileAsync(new DownloadItem(link, archive, null, size, fileName), null, ct)
            .ConfigureAwait(false);

        progress?.Report(new InstallProgress("安装 Java", 0.75, "正在解压运行时"));
        var target = Path.Combine(_paths.RuntimeDirectory, $"java-{major}-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(target);
        await Task.Run(() => ZipFile.ExtractToDirectory(archive, target, true), ct).ConfigureAwait(false);
        var java = Directory.EnumerateFiles(target, "java.exe", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new InvalidDataException("Java 压缩包中缺少 java.exe");
        var runtime = await InspectAsync(java, "自动安装", ct).ConfigureAwait(false)
                      ?? throw new InvalidDataException("已安装的 Java 无法启动");
        progress?.Report(new InstallProgress("Java 就绪", 1, runtime.Display));
        return runtime;
    }
}
