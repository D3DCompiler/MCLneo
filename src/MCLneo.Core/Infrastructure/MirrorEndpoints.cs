namespace MCLneo.Core.Infrastructure;

/// <summary>Domain-neutral helpers for the Mojang CDN and the BMCLAPI mirror.</summary>
public static class MirrorEndpoints
{
    public const string OfficialManifest =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public const string BmclapiManifest =
        "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    public const string BmclapiBase = "https://bmclapi2.bangbang93.com";

    public static string ManifestUrl(this DownloadSource source) =>
        source == DownloadSource.Bmclapi ? BmclapiManifest : OfficialManifest;

    public static string VersionJsonUrl(this DownloadSource source, string versionId, string officialUrl)
    {
        return source == DownloadSource.Bmclapi
            ? $"{BmclapiBase}/version/{Uri.EscapeDataString(versionId)}/json"
            : officialUrl;
    }

    public static string ClientJarUrl(this DownloadSource source, string versionId, string officialUrl)
    {
        return source == DownloadSource.Bmclapi
            ? $"{BmclapiBase}/version/{Uri.EscapeDataString(versionId)}/client"
            : officialUrl;
    }

    public static string Rewrite(this DownloadSource source, string url)
    {
        if (source != DownloadSource.Bmclapi || string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var pathAndQuery = uri.PathAndQuery;
        return uri.Host.ToLowerInvariant() switch
        {
            "launchermeta.mojang.com" or "piston-meta.mojang.com" or "launcher.mojang.com" =>
                BmclapiBase + pathAndQuery,
            "libraries.minecraft.net" => BmclapiBase + "/maven" + pathAndQuery,
            "resources.download.minecraft.net" => BmclapiBase + "/assets" + pathAndQuery,
            "maven.fabricmc.net" => BmclapiBase + "/maven" + pathAndQuery,
            "maven.quiltmc.org" => BmclapiBase + "/maven" + pathAndQuery,
            _ => url
        };
    }

    public static string MavenPath(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            throw new FormatException($"无效的 Maven 坐标：{coordinate}");
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public static string MavenUrl(string baseUrl, string coordinate)
    {
        var root = baseUrl.TrimEnd('/');
        return root + "/" + MavenPath(coordinate);
    }
}
