using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MCLneo.Core.Accounts;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Minecraft;

public sealed record LaunchOptions
{
    public required PlayerProfile Player { get; init; }
    public int MemoryMb { get; init; } = 4096;
    public int Width { get; init; } = 854;
    public int Height { get; init; } = 480;
    public string? ServerAddress { get; init; }
    public int? ServerPort { get; init; }
    public string? ExtraJvmArgs { get; init; }
    public string? ExtraGameArgs { get; init; }
    public string? JavaPath { get; init; }
    public string? AuthlibServer { get; init; }
    public string? AuthlibJar { get; init; }
    public bool DemoMode { get; init; }
}

public sealed record LaunchProgress(string Stage, string Detail, double Fraction = 0);

public sealed record LaunchPlan(
    GameInstance Instance,
    JavaRuntime Java,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string LogPath,
    IReadOnlyDictionary<string, string> Placeholders);

public sealed class GameSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly object _writeGate = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GameSession(Process process, string logPath, StreamWriter writer)
    {
        _process = process;
        LogPath = logPath;
        _writer = writer;
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnOutput;
        process.Exited += OnExited;
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    public string LogPath { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public Task<int> ExitTask => _exit.Task;

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        lock (_writeGate)
        {
            try
            {
                _writer.WriteLine(e.Data);
                _writer.Flush();
            }
            catch
            {
            }
        }

        try
        {
            OutputReceived?.Invoke(e.Data);
        }
        catch
        {
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var code = -1;
        try
        {
            code = _process.ExitCode;
        }
        catch
        {
        }

        lock (_writeGate)
        {
            try
            {
                _writer.WriteLine($"[MCLneo] 游戏进程退出，代码 {code}");
                _writer.Flush();
                _writer.Dispose();
            }
            catch
            {
            }
        }

        _exit.TrySetResult(code);
        try
        {
            Exited?.Invoke(code);
        }
        catch
        {
        }
    }

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _process.OutputDataReceived -= OnOutput;
        _process.ErrorDataReceived -= OnOutput;
        _process.Exited -= OnExited;
        _process.Dispose();
    }
}

public sealed class LaunchService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly JavaService _java;
    private readonly AppLog _log;

    public LaunchService(AppPaths paths, SettingsService settings, JavaService java, AppLog log)
    {
        _paths = paths;
        _settings = settings;
        _java = java;
        _log = log;
    }

    public async Task<LaunchPlan> PrepareAsync(
        GameInstance instance,
        LaunchOptions options,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken ct = default)
    {
        instance.EnsureDirectories();
        var source = _settings.Current.DownloadSource;
        Report(progress, "解析版本", "读取版本继承关系", 0.02);
        var version = await VersionResolver.LoadEffectiveAsync(instance, ct).ConfigureAwait(false);

        var requiredMajor = ReadJavaMajor(version, instance.MinecraftVersion);
        Report(progress, "准备 Java", $"需要 Java {requiredMajor}", 0.05);
        var java = await _java.EnsureAsync(requiredMajor, options.JavaPath ?? _settings.Current.JavaPath,
            new ScaledProgress(progress, 0.05, 0.22), ct).ConfigureAwait(false);

        var assetsIndex = ReadAssetIndexId(version)
                          ?? throw new InvalidDataException("版本配置缺少 assets/index，无法启动");
        var assetsRoot = Path.Combine(_settings.Current.GameRoot, "assets");
        var indexPath = Path.Combine(assetsRoot, "indexes", assetsIndex + ".json");
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                $"资源索引 {assetsIndex}.json 不完整，请在版本管理中选择修复或重新安装。", indexPath);
        }

        var libraries = LibraryResolver.Resolve(version, source);
        var nativesDir = Path.Combine(instance.Directory, "natives");
        Report(progress, "解压运行库", "准备本地库目录", 0.25);
        PrepareNatives(libraries, nativesDir, _settings.Current.GameRoot);

        var classpath = new List<string>();
        foreach (var library in libraries)
        {
            var path = LibraryResolver.GetLibraryPath(_settings.Current.GameRoot, library);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"运行库缺失：{library.Name}。请重新安装该实例。", path);
            }

            classpath.Add(path);
        }

        var clientJar = ResolveClientJar(instance, version);
        if (clientJar is null)
        {
            throw new FileNotFoundException("客户端 jar 缺失，请重新安装基础版本", instance.ClientJarPath);
        }

        // Forge/NeoForge replace the vanilla client with a patched client that
        // is injected by their bootstrap. Keeping the vanilla jar on the module
        // path makes NeoForge fail with "modules minecraft and ... export package".
        var launchMainClass = version["mainClass"]?.GetValue<string>() ?? "";
        var forgeLike = launchMainClass.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                        launchMainClass.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase) ||
                        libraries.Any(l =>
                            l.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase) ||
                            l.Name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase));
        if (!forgeLike && !classpath.Contains(clientJar, StringComparer.OrdinalIgnoreCase))
        {
            classpath.Add(clientJar);
        }

        Report(progress, "构建参数", "正在生成启动命令", 0.32);
        var loggingPath = ResolveLoggingPath(version, _settings.Current.GameRoot, out var loggingId);
        var placeholders = BuildPlaceholders(instance, options, java, assetsRoot, assetsIndex,
            nativesDir, classpath, loggingPath, version);
        var features = new LaunchFeatures(
            IsDemoUser: options.DemoMode,
            HasCustomResolution: options.Width > 0 && options.Height > 0);

        var mainClass = version["mainClass"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new InvalidDataException("版本配置缺少 mainClass，无法启动");
        }

        var jvmArguments = BuildJvmArguments(version, options, placeholders, features);
        if (!string.IsNullOrWhiteSpace(options.AuthlibServer) &&
            !string.IsNullOrWhiteSpace(options.AuthlibJar) &&
            File.Exists(options.AuthlibJar))
        {
            jvmArguments.Insert(0, $"-javaagent:{options.AuthlibJar}={options.AuthlibServer.TrimEnd('/')}");
        }
        var gameArguments = BuildGameArguments(version, options, placeholders, features,
            instance.MinecraftVersion);
        var allArguments = new List<string>();
        allArguments.AddRange(jvmArguments);
        allArguments.Add(mainClass);
        allArguments.AddRange(gameArguments);

        var logName = $"mclneo-game-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        var logPath = Path.Combine(instance.LogsDirectory, logName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        _log.Info($"启动实例：{instance.Name}；Java：{java.ExecutablePath}；资源索引：{assetsIndex}；日志配置：{loggingId ?? "默认"}");
        Report(progress, "准备完成", "即将进入游戏", 0.4);
        return new LaunchPlan(instance, java, allArguments, instance.Directory, logPath, placeholders);
    }

    public async Task<GameSession> LaunchAsync(
        GameInstance instance,
        LaunchOptions options,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var plan = await PrepareAsync(instance, options, progress, ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(plan.Java.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = plan.WorkingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["TMP"] = _paths.TempDirectory;
        psi.Environment["TEMP"] = _paths.TempDirectory;
        foreach (var argument in plan.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var writer = new StreamWriter(plan.LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync($"=== MCLneo 启动 {DateTimeOffset.Now:O} ===").ConfigureAwait(false);
        await writer.WriteLineAsync($"实例：{instance.Name}（{instance.Id}）").ConfigureAwait(false);
        await writer.WriteLineAsync($"Java：{plan.Java.ExecutablePath}").ConfigureAwait(false);
        var token = options.Player.AccessToken;
        var safeArgs = plan.Arguments.Select(a => a == token ? "***" : a);
        await writer.WriteLineAsync("命令：" + Environment.NewLine + string.Join(" ", safeArgs.Select(QuoteForLog)))
            .ConfigureAwait(false);

        Report(progress, "启动游戏", "正在创建游戏进程", 0.45);
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        if (process is null)
        {
            writer.Dispose();
            throw new InvalidOperationException("无法创建 Java 进程");
        }

        _log.Info($"游戏进程已启动，PID {process.Id}，日志 {plan.LogPath}");
        Report(progress, "游戏已启动", "Minecraft 正在加载", 0.5);
        return new GameSession(process, plan.LogPath, writer);
    }

    private static int ReadJavaMajor(JsonObject version, string minecraftVersion)
    {
        var node = version["javaVersion"]?["majorVersion"];
        if (node is not null)
        {
            try
            {
                return node.GetValue<int>();
            }
            catch
            {
                if (int.TryParse(node.ToString(), out var parsed)) return parsed;
            }
        }

        return JavaService.RequiredMajor(minecraftVersion);
    }

    private static string? ReadAssetIndexId(JsonObject version)
    {
        var id = version["assetIndex"]?["id"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(id) ? version["assets"]?.GetValue<string>() : id;
    }

    private string? ResolveClientJar(GameInstance instance, JsonObject effective)
    {
        if (File.Exists(instance.ClientJarPath))
        {
            return Path.GetFullPath(instance.ClientJarPath);
        }

        var versionsRoot = Directory.GetParent(instance.Directory)?.FullName;
        if (versionsRoot is null)
        {
            return null;
        }

        var candidates = new List<string>();
        var jarField = effective["jar"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(jarField)) candidates.Add(jarField);
        candidates.Add(instance.MinecraftVersion);
        candidates.Add(instance.Id);
        var current = instance.VersionJsonPath;
        var guard = 0;
        while (File.Exists(current) && guard++ < 10)
        {
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(current)) as JsonObject;
                var parent = json?["inheritsFrom"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(parent)) break;
                candidates.Add(parent);
                current = Path.Combine(versionsRoot, parent, parent + ".json");
            }
            catch
            {
                break;
            }
        }

        candidates.Add(effective["id"]?.GetValue<string>() ?? "");
        foreach (var id in candidates.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(versionsRoot, id, id + ".jar");
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static string? ResolveLoggingPath(JsonObject version, string gameRoot, out string? loggingId)
    {
        loggingId = version["logging"]?["client"]?["file"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(loggingId))
        {
            return null;
        }

        var path = Path.Combine(gameRoot, "assets", "log_configs", loggingId);
        return File.Exists(path) ? path : null;
    }

    private Dictionary<string, string> BuildPlaceholders(
        GameInstance instance,
        LaunchOptions options,
        JavaRuntime java,
        string assetsRoot,
        string assetsIndex,
        string nativesDir,
        IReadOnlyList<string> classpath,
        string? loggingPath,
        JsonObject version)
    {
        var player = options.Player;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = player.Name,
            ["version_name"] = instance.Id,
            ["version_type"] = version["type"]?.GetValue<string>() ?? "release",
            ["game_directory"] = instance.Directory,
            ["assets_root"] = assetsRoot,
            ["assets_index_name"] = assetsIndex,
            ["auth_uuid"] = player.Uuid,
            ["auth_access_token"] = player.AccessToken,
            ["auth_session"] = $"token:{player.AccessToken}:{player.Uuid}",
            ["user_type"] = player.UserType,
            ["auth_xuid"] = player.Xuid ?? "",
            ["clientid"] = player.ClientId ?? "",
            ["user_properties"] = "{}",
            ["launcher_name"] = "MCLneo",
            ["launcher_version"] = "0.1.0",
            ["natives_directory"] = nativesDir,
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine(_settings.Current.GameRoot, "libraries"),
            ["game_assets"] = Path.Combine(assetsRoot, "virtual", assetsIndex),
            ["resolution_width"] = options.Width.ToString(),
            ["resolution_height"] = options.Height.ToString(),
            ["quickPlayPath"] = Path.Combine(instance.Directory, "quickPlay", "quickPlay.log"),
            ["java"] = java.ExecutablePath
        };

        if (!string.IsNullOrWhiteSpace(loggingPath))
        {
            values["path"] = loggingPath;
        }
        else
        {
            values.Remove("path");
        }

        return values;
    }

    private List<string> BuildJvmArguments(
        JsonObject version,
        LaunchOptions options,
        IReadOnlyDictionary<string, string> placeholders,
        LaunchFeatures features)
    {
        var args = VersionRules.ReadArguments(version["arguments"]?["jvm"] as JsonArray, features)
            .Select(a => Substitute(a, placeholders))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        if (args.Count == 0)
        {
            args.Add("-Djava.library.path=${natives_directory}");
            args.Add("-cp");
            args.Add("${classpath}");
            args = args.Select(a => Substitute(a, placeholders)).Where(a => a is not null).Select(a => a!).ToList();
        }

        var result = new List<string>
        {
            $"-Xmx{Math.Clamp(options.MemoryMb, 512, 65536)}M",
            $"-Xms{Math.Min(512, Math.Clamp(options.MemoryMb, 512, 65536))}M",
            "-Dfile.encoding=UTF-8",
            "-Dminecraft.launcher.brand=MCLneo",
            "-Dminecraft.launcher.version=0.1.0",
            "-Dlog4j2.formatMsgNoLookups=true"
        };
        result.AddRange(args);
        if (!result.Any(a => a == "-Djava.library.path" || a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
        {
            result.Add("-Djava.library.path=" + placeholders["natives_directory"]);
        }

        if (!result.Any(a => a == "-cp" || a == "-classpath"))
        {
            result.Add("-cp");
            result.Add(placeholders["classpath"]);
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraJvmArgs))
        {
            result.AddRange(CommandLine.Split(options.ExtraJvmArgs));
        }

        return result;
    }

    private List<string> BuildGameArguments(
        JsonObject version,
        LaunchOptions options,
        IReadOnlyDictionary<string, string> placeholders,
        LaunchFeatures features,
        string minecraftVersion)
    {
        var raw = VersionRules.ReadArguments(version["arguments"]?["game"] as JsonArray, features);
        if (raw.Count == 0 && version["minecraftArguments"]?.GetValue<string>() is { } legacy)
        {
            raw = CommandLine.Split(legacy);
        }

        if (raw.Count == 0)
        {
            raw = new List<string>
            {
                "--username", "${auth_player_name}",
                "--version", "${version_name}",
                "--gameDir", "${game_directory}",
                "--assetsDir", "${assets_root}",
                "--assetIndex", "${assets_index_name}",
                "--uuid", "${auth_uuid}",
                "--accessToken", "${auth_access_token}",
                "--userType", "${user_type}",
                "--versionType", "${version_type}"
            };
        }

        var args = raw.Select(a => Substitute(a, placeholders))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        if (options.Width > 0 && options.Height > 0 &&
            !args.Any(a => string.Equals(a, "--width", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add("--width");
            args.Add(options.Width.ToString());
            args.Add("--height");
            args.Add(options.Height.ToString());
        }

        if (options.DemoMode && !args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--demo");
        }

        if (!string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            var port = options.ServerPort is > 0 ? options.ServerPort.Value : 25565;
            var endpoint = $"{options.ServerAddress}:{port}";
            if (IsModernVersion(minecraftVersion))
            {
                if (!args.Contains("--quickPlayMultiplayer", StringComparer.OrdinalIgnoreCase))
                {
                    args.Add("--quickPlayMultiplayer");
                    args.Add(endpoint);
                }
            }
            else
            {
                args.Add("--server");
                args.Add(options.ServerAddress);
                args.Add("--port");
                args.Add(port.ToString());
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraGameArgs))
        {
            args.AddRange(CommandLine.Split(options.ExtraGameArgs));
        }

        return args;
    }

    private static bool IsModernVersion(string version)
    {
        if (Version.TryParse(version, out var parsed))
        {
            return parsed.Major > 1 || parsed.Major == 1 && parsed.Minor >= 20;
        }

        return true;
    }

    private static string? Substitute(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(value, @"\$\{([^}]+)\}", match =>
        {
            return placeholders.TryGetValue(match.Groups[1].Value, out var replacement)
                ? replacement
                : match.Value;
        });

        return result.Contains("${", StringComparison.Ordinal) ? null : result;
    }

    private static void PrepareNatives(
        IReadOnlyList<ResolvedLibrary> libraries,
        string nativesDir,
        string gameRoot)
    {
        if (Directory.Exists(nativesDir))
        {
            Directory.Delete(nativesDir, true);
        }

        Directory.CreateDirectory(nativesDir);
        foreach (var library in libraries.Where(l => l.IsNative))
        {
            var path = Path.Combine(gameRoot, "libraries",
                library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"本地运行库缺失：{library.Name}", path);
            }

            ExtractNativeArchive(path, nativesDir, library.ExtractExclude);
        }
    }

    private static void ExtractNativeArchive(string jarPath, string nativesDir, IReadOnlyList<string> excludes)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excludes.Any(e => entry.FullName.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var target = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
                entry.ExtractToFile(target, true);
            }
        }
        catch (InvalidDataException)
        {
            // Some native entries are plain jars without native payloads.
        }
    }

    private static string QuoteForLog(string value) =>
        value.Contains(' ') || value.Contains('"') ? '"' + value.Replace("\"", "\\\"") + '"' : value;

    private static void Report(IProgress<LaunchProgress>? progress, string stage, string detail, double fraction)
    {
        progress?.Report(new LaunchProgress(stage, detail, Math.Clamp(fraction, 0, 1)));
    }

    private sealed class ScaledProgress : IProgress<InstallProgress>
    {
        private readonly IProgress<LaunchProgress>? _inner;
        private readonly double _start;
        private readonly double _end;

        public ScaledProgress(IProgress<LaunchProgress>? inner, double start, double end)
        {
            _inner = inner;
            _start = start;
            _end = end;
        }

        public void Report(InstallProgress value)
        {
            _inner?.Report(new LaunchProgress(value.Stage, value.Detail,
                _start + (_end - _start) * Math.Clamp(value.Fraction, 0, 1)));
        }
    }
}

public static class CommandLine
{
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
