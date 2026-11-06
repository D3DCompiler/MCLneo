using System.Text.Json.Nodes;

namespace MCLneo.Core.Models;

public sealed record MinecraftVersionInfo(
    string Id,
    string Type,
    string Url,
    DateTimeOffset ReleaseTime,
    bool IsLatestRelease = false,
    bool IsLatestSnapshot = false);

public sealed class VersionManifest
{
    public JsonObject? Root { get; init; }
    public IReadOnlyList<MinecraftVersionInfo> Versions { get; init; } = Array.Empty<MinecraftVersionInfo>();
    public DateTimeOffset FetchedAt { get; init; }
}

public sealed record InstallProgress(
    string Stage,
    double Fraction,
    string Detail,
    int FilesDone = 0,
    int FilesTotal = 0,
    long BytesDone = 0,
    long BytesTotal = 0);
