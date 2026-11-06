using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Updates;

public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? Notes { get; set; }
    public bool IsInstaller { get; set; }
}

public sealed record StagedUpdate(string Version, string StagingDirectory, string ArchivePath);

/// <summary>
/// Downloads a ZIP update, extracts it to a staging directory and launches a
/// detached cmd helper which waits for this process to exit before replacing files.
/// </summary>
public sealed class UpdateService
{
    public const string GitHubReleaseUrl =
        "https://api.github.com/repos/D3DCompiler/MCLneo/releases/latest";

    public static string CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly AppLog _log;

    public UpdateService(AppPaths paths, HttpService http, AppLog log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    public async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return null;
        }

        string json;
        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            json = await File.ReadAllTextAsync(uri.LocalPath, ct).ConfigureAwait(false);
        }
        else if (File.Exists(manifestUrl))
        {
            json = await File.ReadAllTextAsync(manifestUrl, ct).ConfigureAwait(false);
        }
        else
        {
            json = await _http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);
        }

        var info = TryParseGithubRelease(json);
        info ??= JsonSerializer.Deserialize<UpdateInfo>(json, JsonDefaults.Options);
        if (info is null || string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Url))
        {
            throw new InvalidDataException("更新清单缺少 version/url");
        }

        if (!Version.TryParse(info.Version, out var remote) ||
            !Version.TryParse(CurrentVersion, out var current) || remote <= current)
        {
            return null;
        }

        return info;
    }

    private static UpdateInfo? TryParseGithubRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                !root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var version = (tagElement.GetString() ?? "").Trim().TrimStart('v', 'V');
            if (!Version.TryParse(version, out _)) return null;

            string? url = null;
            var installer = false;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? ""
                    : "";
                var candidate = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = candidate;
                    installer = true;
                    break;
                }

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && url is null)
                {
                    url = candidate;
                }
            }

            if (string.IsNullOrWhiteSpace(url)) return null;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            return new UpdateInfo
            {
                Version = version,
                Url = url,
                Notes = notes,
                IsInstaller = installer
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateInfo info,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var updateRoot = Path.Combine(_paths.AppRoot, "updates");
        Directory.CreateDirectory(updateRoot);
        var destination = Path.Combine(updateRoot, $"MCLneo-{info.Version}-Setup.exe");
        progress?.Report(new InstallProgress("下载更新", 0.02, info.Url));
        using var response = await _http.Client.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            progress?.Report(new InstallProgress("下载更新",
                total > 0 ? received / (double)total * 0.9 : 0.4,
                $"{received / 1024 / 1024} MB"));
        }

        progress?.Report(new InstallProgress("下载完成", 1, destination));
        return destination;
    }

    public async Task<StagedUpdate> DownloadAndStageAsync(
        UpdateInfo info,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var updateRoot = Path.Combine(_paths.AppRoot, "updates");
        Directory.CreateDirectory(updateRoot);
        var archive = Path.Combine(updateRoot, $"MCLneo-{info.Version}.zip");
        progress?.Report(new InstallProgress("下载更新", 0.02, info.Url));
        var localSource = info.Url;
        if (Uri.TryCreate(info.Url, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
        {
            localSource = fileUri.LocalPath;
        }

        if (File.Exists(localSource))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = File.OpenRead(localSource))
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                }
            }

            var localHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(info.Sha256) &&
                !string.Equals(localHash, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"更新包 SHA-256 校验失败（实际 {localHash}）");
            }

            progress?.Report(new InstallProgress("下载更新", 0.6, "本地更新包就绪"));
        }
        else
        {
            using var response = await _http.Client.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                progress?.Report(new InstallProgress("下载更新", total > 0 ? received / (double)total * 0.6 : 0.3,
                    $"{received / 1024 / 1024} MB"));
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(info.Sha256) &&
                !string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"更新包 SHA-256 校验失败（实际 {actual}）");
            }
        }

        var staging = Path.Combine(updateRoot, "staging-" + info.Version);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        Directory.CreateDirectory(staging);
        progress?.Report(new InstallProgress("解压更新", 0.7, staging));
        await Task.Run(() => ZipFile.ExtractToDirectory(archive, staging, true), ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("更新就绪", 1, "退出启动器后应用"));
        _log.Info($"更新已准备：{info.Version} -> {staging}");
        return new StagedUpdate(info.Version, staging, archive);
    }

    /// <summary>Starts the detached replacement helper. The caller must exit immediately after.</summary>
    public void LaunchInstaller(StagedUpdate update, string executablePath)
    {
        var target = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        var script = Path.Combine(_paths.AppRoot, "updates", $"apply-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        var content =
            "@echo off\r\n" +
            "setlocal\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"robocopy \"{update.StagingDirectory}\" \"{target}\" /E /R:2 /W:2 >nul\r\n" +
            $"start \"\" \"{executablePath}\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(script, content, Encoding.Default);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        Process.Start(psi);
    }
}
