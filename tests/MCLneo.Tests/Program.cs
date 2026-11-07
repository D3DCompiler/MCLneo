using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCLneo.Core;
using MCLneo.Core.Accounts;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Instances;
using MCLneo.Core.Logs;
using MCLneo.Core.Minecraft;
using MCLneo.Core.Models;
using MCLneo.Core.Modpacks;
using MCLneo.Core.Mods;
using MCLneo.Core.Rooms;
using MCLneo.Core.Servers;
using MCLneo.Core.Worlds;

namespace MCLneo.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0] == "--install")
        {
            return await InstallSmokeAsync(args);
        }

        if (args.Length > 1 && args[0] == "--launch")
        {
            return await LaunchSmokeAsync(args[1]);
        }

        if (args.Length > 1 && args[0] == "--make-world")
        {
            return MakeWorld(args[1]);
        }

        if (args.Length > 2 && args[0] == "--install-loader")
        {
            return await InstallLoaderAsync(args);
        }

        if (args.Length > 2 && args[0] == "--install-modpack")
        {
            return await InstallModpackAsync(args);
        }

        if (args.Length > 0 && args[0] == "--ensure-injector")
        {
            return await EnsureInjectorAsync();
        }

        if (args.Length > 1 && args[0] == "--update-check")
        {
            return await UpdateCheckAsync(args[1]);
        }

        Run("镜像 URL 重写", TestMirrorRewrite);
        Run("离线 UUID 稳定性", TestOfflineUuid);
        Run("房间邀请码往返", TestInviteRoundTrip);
        Run("房间邀请码拒绝损坏数据", TestInviteTamper);
        Run("命令行参数拆分", TestCommandLineSplit);
        Run("版本继承合并", TestVersionMerge);
        Run("Modrinth Facet 生成", TestFacets);
        Run("Java 版本推断", TestJavaMajor);
        Run("原生库架构筛选", TestNativeArchitecture);
        Run("NBT level.dat 解析", TestNbt);
        Run("存档导出与导入", TestWorldRoundTrip);
        Run("实例导出与导入", TestInstanceRoundTrip);
        Run("服务器列表往返", TestServerListRoundTrip);
        Run("崩溃报告特征分析", TestCrashAnalyzer);
        Run("第三方认证地址与 NeoForge 版本映射", TestAccountHelpers);
        Run("路径越界保护", TestPathSafety);
        Run("本地玩家日志解析", TestLocalPlayerLog);
        await RunAsync("服务器状态协议", TestServerStatusProtocol);
        await RunAsync("IPv6 直连中转", TestIpv6DirectRelay);

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name}: {ex.Message}");
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string TestPath(string kind)
    {
        var root = @"D:\MCLneo\artifacts\tests-temp";
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"mclneo-{kind}-{Guid.NewGuid():N}");
    }

    private static void TestMirrorRewrite()
    {
        Assert(MirrorEndpoints.OfficialManifest.StartsWith("https://piston-meta.mojang.com"),
            "官方清单地址错误");
        Assert(DownloadSource.Official.Rewrite("https://libraries.minecraft.net/a/b.jar") ==
               "https://libraries.minecraft.net/a/b.jar", "官方源不应重写");
        Assert(DownloadSource.Bmclapi.Rewrite("https://libraries.minecraft.net/a/b.jar") ==
               "https://bmclapi2.bangbang93.com/maven/a/b.jar", "运行库镜像重写错误");
        Assert(DownloadSource.Bmclapi.Rewrite("https://resources.download.minecraft.net/aa/bb") ==
               "https://bmclapi2.bangbang93.com/assets/aa/bb", "资源镜像重写错误");
    }

    private static void TestOfflineUuid()
    {
        var uuid = PlayerProfile.CreateOfflineUuid("Steve");
        Assert(uuid.Length == 32, "UUID 长度错误");
        Assert(uuid == PlayerProfile.CreateOfflineUuid("Steve"), "UUID 不稳定");
        Assert(uuid != PlayerProfile.CreateOfflineUuid("Alex"), "不同玩家 UUID 不应相同");
        Assert(uuid[12] == '3', "UUID version 应为 3");
        Assert("89ab".Contains(uuid[16]), "UUID variant 错误");
    }

    private static void TestInviteRoundTrip()
    {
        var original = RoomInvite.Create("tcp://public.easytier.top:11010", 54321);
        var code = original.Encode();
        Assert(code.StartsWith(RoomInvite.Prefix), "邀请码前缀错误");
        Assert(code.All(c => c is not ('+' or '/' or '=')), "邀请码不是 base64url");
        Assert(RoomInvite.TryDecode(code, out var decoded, out var error), "无法解码：" + error);
        Assert(decoded.NetworkName == original.NetworkName, "网络名不一致");
        Assert(decoded.Secret == original.Secret, "密钥不一致");
        Assert(decoded.GamePort == 54321, "端口不一致");
    }

    private static void TestInviteTamper()
    {
        Assert(!RoomInvite.TryDecode("hello", out _, out _), "无效邀请码不应通过");
        Assert(!RoomInvite.TryDecode(RoomInvite.Prefix + "%%%%", out _, out _), "损坏 base64 不应通过");
        var bad = new RoomInvite
        {
            NetworkName = "mcl-valid",
            Secret = "123456",
            Peer = "not-a-peer",
            GamePort = 25565
        }.Encode();
        Assert(!RoomInvite.TryDecode(bad, out _, out var error), "非法中继地址不应通过：" + error);
    }

    private static void TestCommandLineSplit()
    {
        var parts = CommandLine.Split("--width 1280 --title \"Hello World\" --demo");
        Assert(parts.Count == 5, "参数数量错误");
        Assert(parts[3] == "Hello World", "引号解析错误");
    }

    private static void TestVersionMerge()
    {
        var parent = JsonNode.Parse("""
        {"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","libraries":[
          {"name":"g:a:1"},{"name":"g:b:1"}],"arguments":{"game":["--username","${auth_player_name}"],
          "jvm":["-cp","${classpath}"]},"assetIndex":{"id":"5"}}
        """)!.AsObject();
        var child = JsonNode.Parse("""
        {"id":"fabric","inheritsFrom":"1.20.1","mainClass":"fabric.Main","libraries":[
          {"name":"g:a:2"},{"name":"g:c:1"}],"arguments":{"jvm":["-DFabric=1"]}}
        """)!.AsObject();
        var merged = VersionResolver.Merge(parent, child);
        Assert(merged["id"]!.GetValue<string>() == "fabric", "子版本 id 未覆盖");
        Assert(merged["mainClass"]!.GetValue<string>() == "fabric.Main", "主类未覆盖");
        Assert(merged["inheritsFrom"] is null, "inheritsFrom 应被移除");
        var libs = merged["libraries"]!.AsArray();
        Assert(libs.Count == 3, "运行库合并数量错误");
        Assert(libs[0]!["name"]!.GetValue<string>() == "g:a:2", "子运行库未覆盖父运行库");
        var jvm = merged["arguments"]!["jvm"]!.AsArray();
        Assert(jvm.Count == 3, "JVM 参数合并错误");
        Assert(merged["assetIndex"]!["id"]!.GetValue<string>() == "5", "资源索引未继承");
    }

    private static void TestFacets()
    {
        var facets = ModrinthService.BuildFacets("mod", "1.21.4", "fabric");
        Assert(facets.Count == 3, "Facet 组数量错误");
        var json = System.Text.Json.JsonSerializer.Serialize(facets);
        Assert(json.Contains("versions:1.21.4") && json.Contains("categories:fabric"), "Facet 内容错误");
        var vanilla = ModrinthService.BuildFacets("mod", "1.21.4", "vanilla");
        Assert(vanilla.Count == 2, "原版不应添加加载器 Facet");
    }

    private static void TestJavaMajor()
    {
        Assert(JavaService.RequiredMajor("1.12.2") == 8, "1.12.2 应使用 Java 8");
        Assert(JavaService.RequiredMajor("1.17.1") == 16, "1.17.1 应使用 Java 16");
        Assert(JavaService.RequiredMajor("1.18.2") == 17, "1.18.2 应使用 Java 17");
        Assert(JavaService.RequiredMajor("1.20.4") == 17, "1.20.4 应使用 Java 17");
        Assert(JavaService.RequiredMajor("1.20.5") == 21, "1.20.5 应使用 Java 21");
        Assert(JavaService.RequiredMajor("1.21.4") == 21, "1.21.4 应使用 Java 21");
    }

    private static void TestNativeArchitecture()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        Assert(!VersionRules.IsForeignNativeLibrary("org.lwjgl:lwjgl:3.3.3:natives-windows"),
            "x64 原生库被错误排除");
        Assert(VersionRules.IsForeignNativeLibrary("org.lwjgl:lwjgl:3.3.3:natives-windows-arm64"),
            "arm64 原生库未排除");
        Assert(VersionRules.IsForeignNativeLibrary("org.lwjgl:lwjgl:3.3.3:natives-linux"),
            "Linux 原生库未排除");
    }

    private static void TestNbt()
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, true))
        {
            WriteNbtFixture(gzip);
        }

        stream.Position = 0;
        using var gzipRead = new GZipStream(stream, CompressionMode.Decompress);
        var nbt = NbtReader.Read(gzipRead);
        var data = (Dictionary<string, object?>)nbt["Data"]!;
        Assert((string)data["LevelName"]! == "测试世界", "LevelName 解析错误");
        Assert(data["GameType"] is 1, "GameType 解析错误");
        var version = (Dictionary<string, object?>)data["Version"]!;
        Assert((string)version["Name"]! == "1.21.4", "Version.Name 解析错误");
    }

    private static void WriteNbtFixture(Stream stream)
    {
        stream.WriteByte(10); WriteString(stream, "");
        stream.WriteByte(10); WriteString(stream, "Data");
        stream.WriteByte(8); WriteString(stream, "LevelName"); WriteString(stream, "测试世界");
        stream.WriteByte(3); WriteString(stream, "GameType"); WriteInt(stream, 1);
        stream.WriteByte(4); WriteString(stream, "LastPlayed"); WriteLong(stream, 1700000000000);
        stream.WriteByte(1); WriteString(stream, "Hardcore"); stream.WriteByte(0);
        stream.WriteByte(10); WriteString(stream, "Version");
        stream.WriteByte(8); WriteString(stream, "Name"); WriteString(stream, "1.21.4");
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteLong(Stream stream, long value)
    {
        for (var i = 7; i >= 0; i--)
        {
            stream.WriteByte((byte)(value >> (i * 8)));
        }
    }

    private static void TestWorldRoundTrip()
    {
        var root = TestPath("roundtrip");
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            var log = new AppLog(paths);
            var service = new WorldService(paths, log);
            var source = CreateTestInstance(Path.Combine(root, "game", "versions", "source"));
            var worldDir = Path.Combine(source.SavesDirectory, "TestWorld");
            Directory.CreateDirectory(worldDir);
            using (var file = File.Create(Path.Combine(worldDir, "level.dat")))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            {
                WriteNbtFixture(gzip);
            }

            File.WriteAllText(Path.Combine(worldDir, "notes.txt"), "hello");
            var worlds = service.GetWorldsAsync(source).GetAwaiter().GetResult();
            Assert(worlds.Count == 1, "未发现存档");
            Assert(worlds[0].Name == "测试世界", "存档名称错误");
            Assert(worlds[0].GameModeText == "创造模式", "游戏模式错误");

            var zip = Path.Combine(root, "world.zip");
            service.ExportAsync(worlds[0], zip).GetAwaiter().GetResult();
            Assert(File.Exists(zip), "存档 zip 未生成");

            var target = CreateTestInstance(Path.Combine(root, "game", "versions", "target"));
            var imported = service.ImportAsync(zip, target).GetAwaiter().GetResult();
            Assert(imported.Name == "测试世界", "导入存档名称错误");
            Assert(File.Exists(Path.Combine(imported.Directory, "level.dat")), "导入缺少 level.dat");
            Assert(File.ReadAllText(Path.Combine(imported.Directory, "notes.txt")) == "hello", "导入文件内容错误");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static GameInstance CreateTestInstance(string directory)
    {
        Directory.CreateDirectory(directory);
        var instance = new GameInstance
        {
            Id = "test",
            Name = "Test",
            Directory = directory,
            MinecraftVersion = "1.21.4"
        };
        instance.EnsureDirectories();
        return instance;
    }

    private static void TestInstanceRoundTrip()
    {
        var root = TestPath("instance");
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            var log = new AppLog(paths);
            var settings = new SettingsService(paths, log);
            settings.LoadAsync().GetAwaiter().GetResult();
            settings.Current.GameRoot = Path.Combine(root, "minecraft");
            var service = new InstanceService(paths, settings, null!, log);

            var sourceDir = Path.Combine(settings.Current.GameRoot, "versions", "MyPack");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "MyPack.json"),
                """{"id":"MyPack","type":"release","mainClass":"example.Main","libraries":[]}""");
            File.WriteAllText(Path.Combine(sourceDir, "mclneo.instance.json"),
                """{"formatVersion":1,"name":"我的整合包","minecraftVersion":"1.21.4","loader":"vanilla"}""");
            Directory.CreateDirectory(Path.Combine(sourceDir, "mods"));
            File.WriteAllText(Path.Combine(sourceDir, "mods", "example.jar"), "not really a jar");
            var instance = new GameInstance
            {
                Id = "MyPack",
                Name = "我的整合包",
                Directory = sourceDir,
                MinecraftVersion = "1.21.4"
            };

            var archive = Path.Combine(root, "pack.mclneopack");
            service.ExportAsync(instance, archive).GetAwaiter().GetResult();
            Assert(File.Exists(archive), "实例包未生成");

            var imported = service.ImportAsync(archive, "导入的整合包").GetAwaiter().GetResult();
            Assert(imported.Name == "导入的整合包", "导入实例名称错误");
            Assert(File.Exists(Path.Combine(imported.Directory, "mods", "example.jar")), "导入后模组文件缺失");
            Assert(File.Exists(Path.Combine(imported.Directory, imported.Id + ".json")), "导入后版本 JSON 缺失");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestServerListRoundTrip()
    {
        var root = TestPath("server");
        try
        {
            Directory.CreateDirectory(root);
            var instance = new GameInstance
            {
                Id = "server-test",
                Name = "Server Test",
                Directory = root,
                MinecraftVersion = "1.21.4"
            };
            instance.EnsureDirectories();
            var entries = new List<ServerEntry>
            {
                new("主服务器", "play.example.com:25565"),
                new("IPv6", "[::1]:25566")
            };
            ServerListService.Write(instance, entries);
            var read = ServerListService.Read(instance);
            Assert(read.Count == 2, "服务器数量错误");
            Assert(read[0].Name == "主服务器" && read[0].Address == "play.example.com", "服务器名称/地址错误");
            Assert(read[0].Port == 25565, "默认端口解析错误");
            Assert(read[1].Address == "::1" && read[1].Port == 25566, "IPv6 解析错误");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestCrashAnalyzer()
    {
        var findings = CrashAnalyzer.Analyze("""
            java.lang.OutOfMemoryError: Java heap space
            java.lang.UnsupportedClassVersionError: class file version 65.0
            The game crashed whilst initializing game
            """);
        Assert(findings.Any(f => f.Title.Contains("内存")), "未识别内存不足");
        Assert(findings.Any(f => f.Title.Contains("Java")), "未识别 Java 版本问题");
        Assert(findings.Any(f => f.Title.Contains("崩溃")), "未识别崩溃报告");
        Assert(CrashAnalyzer.Analyze("normal log").Count == 0, "普通日志不应产生分析结论");
    }

    private static void TestAccountHelpers()
    {
        Assert(AuthlibService.NormalizeServer("littleskin.cn/api/yggdrasil") ==
               "https://littleskin.cn/api/yggdrasil", "authlib 地址补全失败");
        Assert(LoaderInstallerService.NeoForgePrefix("1.21.4") == "21.4", "NeoForge 1.21.4 映射错误");
        Assert(LoaderInstallerService.NeoForgePrefix("1.20.1") == "47.1", "NeoForge 1.20.1 映射错误");
    }

    private static void TestPathSafety()
    {
        var root = TestPath("path");
        Directory.CreateDirectory(root);
        try
        {
            var safe = PathSafety.EnsureInside(root, Path.Combine(root, "a", "b"));
            Assert(safe.EndsWith(Path.Combine("a", "b"), StringComparison.OrdinalIgnoreCase), "合法路径被拒绝");
            var threw = false;
            try
            {
                PathSafety.EnsureInside(root, Path.Combine(root, "..", "escape"));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert(threw, "越界路径未被拒绝");
            Assert(PathSafety.SafeFileName("a<b>c") == "a_b_c", "文件名净化错误");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<int> InstallLoaderAsync(string[] args)
    {
        var mc = args[1];
        var loader = args[2].Equals("neoforge", StringComparison.OrdinalIgnoreCase)
            ? LoaderKind.NeoForge
            : LoaderKind.Forge;
        var rootIndex = Array.IndexOf(args, "--root");
        var nameIndex = Array.IndexOf(args, "--name");
        var versionIndex = Array.IndexOf(args, "--version");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? Path.GetFullPath(args[rootIndex + 1])
            : TestPath("loader");
        var name = nameIndex >= 0 && nameIndex + 1 < args.Length ? args[nameIndex + 1] : $"{loader} Test";
        var version = versionIndex >= 0 && versionIndex + 1 < args.Length ? args[versionIndex + 1] : null;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var settings = new SettingsService(paths, log);
        await settings.LoadAsync();
        settings.Current.GameRoot = Path.Combine(root, "minecraft");
        settings.Current.DownloadSource = DownloadSource.Bmclapi;
        var downloads = new DownloadService(http, log);
        var catalog = new VersionCatalog(paths, http, log);
        var installer = new MinecraftInstaller(paths, settings, downloads, catalog, http, log);
        var java = new JavaService(paths, http, downloads, log);
        var loaders = new LoaderInstallerService(paths, settings, downloads, catalog, http, java, log);
        var manifest = await catalog.GetManifestAsync(DownloadSource.Bmclapi);
        var game = manifest.Versions.FirstOrDefault(v => v.Id == mc);
        if (game is null)
        {
            Console.WriteLine("找不到 Minecraft 版本：" + mc);
            return 2;
        }

        var progress = new Progress<InstallProgress>(p => Console.WriteLine($"  {p.Fraction,5:P0} {p.Stage} · {p.Detail}"));
        var instance = await loaders.InstallAsync(loader, game, name, version, progress);
        Console.WriteLine("安装完成：" + instance.Directory);
        http.Dispose();
        return 0;
    }

    private static async Task<int> InstallModpackAsync(string[] args)
    {
        var archive = Path.GetFullPath(args[1]);
        var name = args[2];
        var rootIndex = Array.IndexOf(args, "--root");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? Path.GetFullPath(args[rootIndex + 1])
            : TestPath("modpack");
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var settings = new SettingsService(paths, log);
        await settings.LoadAsync();
        settings.Current.GameRoot = Path.Combine(root, "minecraft");
        settings.Current.DownloadSource = DownloadSource.Bmclapi;
        var downloads = new DownloadService(http, log);
        var catalog = new VersionCatalog(paths, http, log);
        var installer = new MinecraftInstaller(paths, settings, downloads, catalog, http, log);
        var java = new JavaService(paths, http, downloads, log);
        var loaders = new LoaderInstallerService(paths, settings, downloads, catalog, http, java, log);
        var modrinth = new ModrinthService(http, downloads, log);
        var modpacks = new ModpackService(paths, settings, downloads, catalog, installer, loaders, modrinth, log);
        var progress = new Progress<InstallProgress>(p =>
            Console.WriteLine($"  {p.Fraction,5:P0} {p.Stage} · {p.Detail}"));
        var instance = await modpacks.InstallArchiveAsync(archive, name, name, progress);
        Console.WriteLine("整合包实例：" + instance.Directory);
        http.Dispose();
        return 0;
    }

    private static async Task<int> UpdateCheckAsync(string manifest)
    {
        var paths = new AppPaths(@"D:\MCLneo\data");
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var service = new MCLneo.Core.Updates.UpdateService(paths, http, log);
        var info = await service.CheckAsync(manifest);
        if (info is null)
        {
            Console.WriteLine("已是最新版本");
            http.Dispose();
            return 0;
        }

        Console.WriteLine($"发现 {info.Version}");
        var staged = await service.DownloadAndStageAsync(info, new Progress<InstallProgress>(p =>
            Console.WriteLine($"  {p.Fraction,5:P0} {p.Stage} · {p.Detail}")));
        Console.WriteLine("staging=" + staged.StagingDirectory);
        var ok = Directory.Exists(staged.StagingDirectory) &&
                 Directory.EnumerateFiles(staged.StagingDirectory, "*", SearchOption.AllDirectories).Any();
        http.Dispose();
        return ok ? 0 : 4;
    }

    private static async Task<int> EnsureInjectorAsync()
    {
        var paths = new AppPaths(@"D:\MCLneo\data");
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var service = new AuthlibService(paths, http, log);
        var path = await service.EnsureInjectorAsync(new Progress<InstallProgress>(p =>
            Console.WriteLine($"  {p.Fraction,5:P0} {p.Stage} · {p.Detail}")));
        Console.WriteLine("injector=" + path);
        Console.WriteLine("exists=" + File.Exists(path) + " size=" + new FileInfo(path).Length);
        http.Dispose();
        return File.Exists(path) ? 0 : 3;
    }

    private static int MakeWorld(string instanceDirectory)
    {
        var saves = Path.Combine(instanceDirectory, "saves");
        var world = Path.Combine(saves, "Test World");
        Directory.CreateDirectory(world);
        using (var file = File.Create(Path.Combine(world, "level.dat")))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            WriteNbtFixture(gzip);
        }

        File.WriteAllText(Path.Combine(world, "notes.txt"), "A test world created by MCLneo.");
        Console.WriteLine("Created " + world);
        return 0;
    }

    private static async Task<int> LaunchSmokeAsync(string instanceDirectory)
    {
        instanceDirectory = Path.GetFullPath(instanceDirectory);
        var versionsRoot = Directory.GetParent(instanceDirectory)?.FullName
                           ?? throw new InvalidOperationException("实例目录无效");
        var gameRoot = Directory.GetParent(versionsRoot)?.FullName
                       ?? throw new InvalidOperationException("游戏根目录无效");
        var id = Path.GetFileName(instanceDirectory);
        var jsonPath = Path.Combine(instanceDirectory, id + ".json");
        var profile = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath)) as JsonObject
                      ?? throw new InvalidDataException("实例 JSON 无效");
        var instance = new GameInstance
        {
            Id = id,
            Name = id,
            Directory = instanceDirectory,
            MinecraftVersion = profile["inheritsFrom"]?.GetValue<string>() ?? id
        };
        instance.EnsureDirectories();

        var appRoot = TestPath("launch");
        var paths = new AppPaths(appRoot);
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var settings = new SettingsService(paths, log);
        await settings.LoadAsync();
        settings.Current.GameRoot = gameRoot;
        settings.Current.DownloadSource = DownloadSource.Bmclapi;
        var downloads = new DownloadService(http, log);
        var java = new JavaService(paths, http, downloads, log);
        var launcher = new LaunchService(paths, settings, java, log);
        var progress = new Progress<LaunchProgress>(p =>
            Console.WriteLine($"  {p.Fraction,5:P0}  {p.Stage} · {p.Detail}"));
        try
        {
            var session = await launcher.LaunchAsync(instance, new LaunchOptions
            {
                Player = PlayerProfile.Offline("TestPlayer"),
                MemoryMb = 2048,
                Width = 854,
                Height = 480
            }, progress);
            Console.WriteLine("进程已启动，PID=" + session.ProcessId);
            Console.WriteLine("日志：" + session.LogPath);
            session.OutputReceived += line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("  [game] " + line);
            };
            var exit = await Task.WhenAny(session.ExitTask, Task.Delay(TimeSpan.FromSeconds(45)));
            if (exit == session.ExitTask)
            {
                var code = await session.ExitTask;
                Console.WriteLine("游戏进程提前退出，代码 " + code);
                Console.WriteLine("--- 日志尾部 ---");
                Console.WriteLine(string.Join(Environment.NewLine, File.ReadAllLines(session.LogPath).TakeLast(80)));
                session.Dispose();
                return code == 0 ? 0 : 3;
            }

            Console.WriteLine("游戏进程在 45 秒后仍存活，判定启动成功。");
            session.Stop();
            await Task.WhenAny(session.ExitTask, Task.Delay(5000));
            session.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("启动失败：" + ex);
            return 4;
        }
        finally
        {
            http.Dispose();
        }
    }

    private static void TestLocalPlayerLog()
    {
        var root = TestPath("player-log");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.WriteAllLines(Path.Combine(root, "logs", "latest.log"), new[]
            {
                "[12:00:00] [Server thread/INFO]: Steve joined the game",
                "[12:00:01] [Server thread/INFO]: Alex joined the game",
                "[12:01:00] [Server thread/INFO]: Steve left the game"
            });
            var players = LocalPlayerLogService.ReadOnlinePlayers(root);
            Assert(players.Count == 1 && players[0] == "Alex", "本地玩家日志解析错误");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task TestServerStatusProtocol()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var json = """
                   {"version":{"name":"1.21.4","protocol":769},"players":{"online":2,"max":20,"sample":[{"name":"Steve"},{"name":"Alex"}]},"description":{"text":"MCLneo test"}}
                   """;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[512];
            await stream.ReadAsync(buffer);
            // Drain the status request too; closing with unread input would
            // reset the connection before the client reads our response.
            await Task.Delay(50);
            while (stream.DataAvailable)
            {
                await stream.ReadAsync(buffer);
            }
            using var body = new MemoryStream();
            WriteStatusVarInt(body, 0);
            WriteStatusString(body, json);
            using var packet = new MemoryStream();
            WriteStatusVarInt(packet, (int)body.Length);
            body.Position = 0;
            body.CopyTo(packet);
            await stream.WriteAsync(packet.ToArray());
            await stream.FlushAsync();
        });

        try
        {
            var status = await ServerStatusService.QueryAsync("127.0.0.1", port, 4000);
            Assert(status.Online, status.Error ?? "服务器状态查询失败");
            Assert(status.OnlinePlayers == 2 && status.MaxPlayers == 20, "在线人数解析错误");
            Assert(status.Players.Count == 2 && status.Players.Contains("Alex"), "玩家名单解析错误");
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task TestIpv6DirectRelay()
    {
        var availability = await Ipv6DirectService.CheckAsync();
        Assert(availability.Available && availability.CanBind, availability.Message);
        if (Ipv6DirectService.FindGlobalIpv6() is null)
        {
            throw new InvalidOperationException("当前机器没有可用于测试的公网 IPv6 地址");
        }

        var game = new TcpListener(IPAddress.Loopback, 0);
        game.Start();
        var gamePort = ((IPEndPoint)game.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource();
        var gameServer = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await game.AcceptTcpClientAsync(serverCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var stream = client.GetStream();
                            var buffer = new byte[64];
                            var read = await stream.ReadAsync(buffer, serverCts.Token);
                            if (read > 0)
                            {
                                await stream.WriteAsync(buffer.AsMemory(0, read), serverCts.Token);
                            }
                        }
                        catch
                        {
                        }
                    }
                });
            }
        });

        var service = new Ipv6DirectService();
        Ipv6DirectSession? host = null;
        Ipv6DirectSession? join = null;
        try
        {
            host = await service.HostAsync(gamePort);
            Assert(!string.IsNullOrWhiteSpace(host.ShareAddress), "IPv6 房主地址为空");
            join = await service.JoinAsync(host.ShareAddress!);
            Assert(!string.IsNullOrWhiteSpace(join.ConnectAddress), "IPv6 本地入口为空");
            var localPort = int.Parse(join.ConnectAddress!.Split(':').Last());

            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync("127.0.0.1", localPort, timeout.Token);
            await using var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes("MCLNEO");
            await stream.WriteAsync(payload, timeout.Token);
            var received = new byte[payload.Length];
            await stream.ReadExactlyAsync(received, timeout.Token);
            Assert(received.SequenceEqual(payload), "IPv6 直连中转数据不一致");
        }
        finally
        {
            if (join is not null) await join.StopAsync();
            if (host is not null) await host.StopAsync();
            serverCts.Cancel();
            game.Stop();
            try { await gameServer; } catch { }
        }
    }

    private static void WriteStatusVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;
        do
        {
            var b = (byte)(unsigned & 0x7F);
            unsigned >>= 7;
            if (unsigned != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (unsigned != 0);
    }

    private static void WriteStatusString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteStatusVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static async Task<int> InstallSmokeAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：--install <Minecraft版本> [vanilla|fabric]");
            return 1;
        }

        var versionId = args[1];
        var loader = args.Length > 2 && args[2].Equals("fabric", StringComparison.OrdinalIgnoreCase)
            ? LoaderKind.Fabric
            : LoaderKind.Vanilla;
        var rootIndex = Array.IndexOf(args, "--root");
        var nameIndex = Array.IndexOf(args, "--name");
        var root = rootIndex >= 0 && rootIndex + 1 < args.Length
            ? Path.GetFullPath(args[rootIndex + 1])
            : TestPath("install");
        var instanceName = nameIndex >= 0 && nameIndex + 1 < args.Length ? args[nameIndex + 1] : "Smoke Test";
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var settings = new SettingsService(paths, log);
        await settings.LoadAsync();
        settings.Current.GameRoot = Path.Combine(root, "minecraft");
        settings.Current.DownloadSource = DownloadSource.Bmclapi;
        var downloads = new DownloadService(http, log);
        var catalog = new VersionCatalog(paths, http, log);
        var installer = new MinecraftInstaller(paths, settings, downloads, catalog, http, log);
        var manifest = await catalog.GetManifestAsync(DownloadSource.Bmclapi);
        var version = manifest.Versions.FirstOrDefault(v => v.Id == versionId);
        if (version is null)
        {
            Console.WriteLine("找不到版本：" + versionId);
            return 2;
        }

        var progress = new Progress<InstallProgress>(p =>
        {
            Console.Write($"\r{p.Fraction,6:P1}  {p.Stage,-14}  {p.Detail,-40}");
        });
        var instance = await installer.InstallAsync(new InstallRequest(version, instanceName, loader), progress);
        Console.WriteLine();
        Console.WriteLine("安装完成：" + instance.Directory);
        Console.WriteLine("游戏根目录：" + settings.Current.GameRoot);
        Console.WriteLine("保留测试数据以便检查。");
        http.Dispose();
        return 0;
    }
}
