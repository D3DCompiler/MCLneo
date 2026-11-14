using System.Text.Json.Serialization;
using MCLneo.Core.Infrastructure;

namespace MCLneo.Core.Models;

public sealed class GameInstance
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public LoaderKind Loader { get; set; }
    public string? LoaderVersion { get; set; }
    public required string Directory { get; init; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastPlayed { get; set; }
    public bool IsImported { get; set; }

    [JsonIgnore]
    public string VersionJsonPath => Path.Combine(Directory, Id + ".json");

    [JsonIgnore]
    public string ClientJarPath => Path.Combine(Directory, Id + ".jar");

    [JsonIgnore]
    public string PickerLabel => $"{Name} · {Subtitle}";

    [JsonIgnore]
    public string ModsDirectory => Path.Combine(Directory, "mods");

    [JsonIgnore]
    public string SavesDirectory => Path.Combine(Directory, "saves");

    [JsonIgnore]
    public string ResourcePacksDirectory => Path.Combine(Directory, "resourcepacks");

    [JsonIgnore]
    public string ShaderPacksDirectory => Path.Combine(Directory, "shaderpacks");

    [JsonIgnore]
    public string ConfigDirectory => Path.Combine(Directory, "config");

    [JsonIgnore]
    public string LogsDirectory => Path.Combine(Directory, "logs");

    [JsonIgnore]
    public string DisplayLoader => Loader switch
    {
        LoaderKind.Vanilla => "原版",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => Loader.ToString()
    };

    [JsonIgnore]
    public string Subtitle => Loader == LoaderKind.Vanilla
        ? $"Minecraft {MinecraftVersion}"
        : $"Minecraft {MinecraftVersion} · {DisplayLoader} {LoaderVersion}".TrimEnd();

    public void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(ModsDirectory);
        System.IO.Directory.CreateDirectory(SavesDirectory);
        System.IO.Directory.CreateDirectory(ResourcePacksDirectory);
        System.IO.Directory.CreateDirectory(ShaderPacksDirectory);
        System.IO.Directory.CreateDirectory(ConfigDirectory);
        System.IO.Directory.CreateDirectory(LogsDirectory);
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "crash-reports"));
    }

    public static GameInstance Create(string id, string name, string directory)
    {
        return new GameInstance
        {
            Id = id,
            Name = name,
            Directory = Path.GetFullPath(directory),
            MinecraftVersion = "",
            CreatedAt = DateTimeOffset.Now
        };
    }
}
