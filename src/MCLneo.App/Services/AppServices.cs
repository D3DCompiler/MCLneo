using MCLneo.Core;
using MCLneo.Core.Accounts;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Instances;
using MCLneo.Core.Logs;
using MCLneo.Core.Minecraft;
using MCLneo.Core.Modpacks;
using MCLneo.Core.Mods;
using MCLneo.Core.Rooms;
using MCLneo.Core.Skins;
using MCLneo.Core.Updates;
using MCLneo.Core.Worlds;

namespace MCLneo.App.Services;

/// <summary>Composition root for the desktop application.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(
        AppPaths paths,
        AppLog log,
        HttpService http,
        SettingsService settings,
        DownloadService downloads,
        VersionCatalog catalog,
        MinecraftInstaller installer,
        LoaderInstallerService loaderInstaller,
        InstanceService instances,
        JavaService java,
        AuthlibService authlib,
        AuthlibAccountStore authlibStore,
        SkinService skins,
        LaunchService launch,
        ModrinthService modrinth,
        ModpackService modpacks,
        LogService gameLogs,
        UpdateService updates,
        WorldService worlds,
        EasyTierManager easyTier,
        RoomService rooms,
        Ipv6DirectService directRooms)
    {
        Paths = paths;
        Log = log;
        Http = http;
        Settings = settings;
        Downloads = downloads;
        Catalog = catalog;
        Installer = installer;
        LoaderInstaller = loaderInstaller;
        Instances = instances;
        Java = java;
        Authlib = authlib;
        AuthlibStore = authlibStore;
        Skins = skins;
        Launch = launch;
        Modrinth = modrinth;
        Modpacks = modpacks;
        GameLogs = gameLogs;
        Updates = updates;
        Worlds = worlds;
        EasyTier = easyTier;
        Rooms = rooms;
        DirectRooms = directRooms;
    }

    public AppPaths Paths { get; }
    public AppLog Log { get; }
    public HttpService Http { get; }
    public SettingsService Settings { get; }
    public DownloadService Downloads { get; }
    public VersionCatalog Catalog { get; }
    public MinecraftInstaller Installer { get; }
    public LoaderInstallerService LoaderInstaller { get; }
    public InstanceService Instances { get; }
    public JavaService Java { get; }
    public AuthlibService Authlib { get; }
    public AuthlibAccountStore AuthlibStore { get; }
    public SkinService Skins { get; }
    public LaunchService Launch { get; }
    public ModrinthService Modrinth { get; }
    public ModpackService Modpacks { get; }
    public LogService GameLogs { get; }
    public UpdateService Updates { get; }
    public WorldService Worlds { get; }
    public EasyTierManager EasyTier { get; }
    public RoomService Rooms { get; }
    public Ipv6DirectService DirectRooms { get; }

    public static async Task<AppServices> CreateAsync(CancellationToken ct = default)
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        var log = new AppLog(paths);
        var http = new HttpService();
        var settings = new SettingsService(paths, log);
        await settings.LoadAsync(ct).ConfigureAwait(false);
        var downloads = new DownloadService(http, log);
        var catalog = new VersionCatalog(paths, http, log);
        var installer = new MinecraftInstaller(paths, settings, downloads, catalog, http, log);
        var instances = new InstanceService(paths, settings, installer, log);
        var java = new JavaService(paths, http, downloads, log);
        var authlib = new AuthlibService(paths, http, log);
        var authlibStore = new AuthlibAccountStore(paths);
        var skins = new SkinService(paths, http, log);
        var loaderInstaller = new LoaderInstallerService(paths, settings, downloads, catalog, http, java, log);
        var launch = new LaunchService(paths, settings, java, log);
        var modrinth = new ModrinthService(http, downloads, log);
        var modpacks = new ModpackService(paths, settings, downloads, catalog, installer, loaderInstaller,
            modrinth, log);
        var gameLogs = new LogService();
        var updates = new UpdateService(paths, http, log);
        var worlds = new WorldService(paths, log);
        var easyTier = new EasyTierManager(paths, settings, downloads, http, log);
        var rooms = new RoomService(paths, settings, easyTier, log);
        var directRooms = new Ipv6DirectService();
        log.Info($"MCLneo 启动，数据目录：{paths.AppRoot}，游戏目录：{settings.Current.GameRoot}");
        return new AppServices(paths, log, http, settings, downloads, catalog, installer, loaderInstaller,
            instances, java, authlib, authlibStore, skins, launch, modrinth, modpacks, gameLogs, updates,
            worlds, easyTier, rooms, directRooms);
    }

    public void Dispose()
    {
        Http.Dispose();
    }
}
