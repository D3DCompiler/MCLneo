namespace MCLneo.Core.Models;

public sealed class InstanceMetadata
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public LoaderKind Loader { get; set; }
    public string? LoaderVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastPlayed { get; set; }
    public string Source { get; set; } = "mclneo";
}
