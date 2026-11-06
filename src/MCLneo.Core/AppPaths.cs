using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLneo.Core;

/// <summary>Well-known locations owned by MCLneo. Game data lives elsewhere.</summary>
public sealed class AppPaths
{
    public AppPaths(string appRoot)
    {
        AppRoot = Path.GetFullPath(appRoot);
    }

    public string AppRoot { get; }
    public string SettingsFile => Path.Combine(AppRoot, "settings.json");
    public string AccountsFile => Path.Combine(AppRoot, "accounts.json");
    public string CacheDirectory => Path.Combine(AppRoot, "cache");
    public string LogsDirectory => Path.Combine(AppRoot, "logs");
    public string EasyTierDirectory => Path.Combine(AppRoot, "easytier");
    public string RuntimeDirectory => Path.Combine(AppRoot, "runtimes");
    public string TrashDirectory => Path.Combine(AppRoot, "trash");
    public string TempDirectory => Path.Combine(AppRoot, "temp");

    public static AppPaths CreateDefault()
    {
        var overridden = Environment.GetEnvironmentVariable("MCLNEO_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        // On this workstation all launcher-owned data belongs on D:. On machines
        // without a D: drive, fall back to the conventional roaming profile.
        var dRoot = @"D:\MCLneo\data";
        var root = Directory.Exists(@"D:\")
            ? dRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCLneo");
        return new AppPaths(root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(EasyTierDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}

public enum DownloadSource
{
    Official = 0,
    Bmclapi = 1
}

public enum LoaderKind
{
    Vanilla = 0,
    Fabric = 1,
    Quilt = 2,
    Forge = 3,
    NeoForge = 4
}

public sealed class LauncherSettings
{
    public string GameRoot { get; set; } = DefaultGameRoot;
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public DownloadSource DownloadSource { get; set; } = DownloadSource.Bmclapi;
    public int MaxDownloadThreads { get; set; } = 8;
    public string LastPlayerName { get; set; } = "Steve";
    public string AccentTheme { get; set; } = "blue";
    public string ThemeMode { get; set; } = "dark";
    public string? SelectedInstance { get; set; }
    public string? MicrosoftClientId { get; set; }
    public string? EasyTierPeer { get; set; }
    public string? EasyTierCorePath { get; set; }
    public string? LastRoomInvite { get; set; }
    public string UpdateManifestUrl { get; set; } = "";
    public bool CloseAfterLaunch { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;

    [JsonIgnore]
    public static string DefaultGameRoot => Directory.Exists(@"D:\")
        ? @"D:\MCLneo\game"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public LauncherSettings Normalize()
    {
        if (string.IsNullOrWhiteSpace(GameRoot))
        {
            GameRoot = DefaultGameRoot;
        }

        GameRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(GameRoot));
        MemoryMb = Math.Clamp(MemoryMb, 512, 65536);
        MaxDownloadThreads = Math.Clamp(MaxDownloadThreads, 1, 32);
        if (string.IsNullOrWhiteSpace(LastPlayerName))
        {
            LastPlayerName = "Steve";
        }

        if (string.IsNullOrWhiteSpace(AccentTheme))
        {
            AccentTheme = "blue";
        }

        if (!string.Equals(ThemeMode, "light", StringComparison.OrdinalIgnoreCase))
        {
            ThemeMode = "dark";
        }

        if (WindowWidth < 1080)
        {
            WindowWidth = 1280;
        }

        if (WindowHeight < 680)
        {
            WindowHeight = 820;
        }

        return this;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static readonly JsonSerializerOptions Compact = new(Options)
    {
        WriteIndented = false
    };
}
