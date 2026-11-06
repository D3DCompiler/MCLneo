using System.Text.Json.Nodes;
using MCLneo.Core.Infrastructure;

namespace MCLneo.Core.Minecraft;

public sealed record ResolvedLibrary(
    string Name,
    string RelativePath,
    string Url,
    string? Sha1,
    long Size,
    bool IsNative,
    string? Classifier,
    IReadOnlyList<string> ExtractExclude)
{
    public string Key => RelativePath.Replace('\\', '/').ToLowerInvariant();
}

public static class LibraryResolver
{
    public static IReadOnlyList<ResolvedLibrary> Resolve(JsonObject version, DownloadSource source)
    {
        var result = new List<ResolvedLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (version["libraries"] is not JsonArray libraries)
        {
            return result;
        }

        foreach (var library in libraries.OfType<JsonObject>())
        {
            if (!VersionRules.IsLibraryAllowed(library))
            {
                continue;
            }

            var name = library["name"]?.GetValue<string>() ?? "";
            if (VersionRules.IsForeignNativeLibrary(name))
            {
                continue;
            }

            var downloads = library["downloads"] as JsonObject;
            var artifact = downloads?["artifact"] as JsonObject;
            string? relativePath;
            string? url;
            string? sha1 = null;
            var size = -1L;

            var hasNativeClassifier = VersionRules.TryGetNativeClassifier(library, out var nativeClassifier);
            var classifierNode = hasNativeClassifier
                ? downloads?["classifiers"]?[nativeClassifier] as JsonObject
                : null;

            if (classifierNode is not null)
            {
                relativePath = classifierNode["path"]?.GetValue<string>();
                url = classifierNode["url"]?.GetValue<string>();
                sha1 = classifierNode["sha1"]?.GetValue<string>();
                size = classifierNode["size"]?.GetValue<long>() ?? -1;
            }
            else if (artifact is not null)
            {
                relativePath = artifact["path"]?.GetValue<string>();
                url = artifact["url"]?.GetValue<string>();
                sha1 = artifact["sha1"]?.GetValue<string>();
                size = artifact["size"]?.GetValue<long>() ?? -1;
            }
            else
            {
                relativePath = string.IsNullOrWhiteSpace(name) ? null : MirrorEndpoints.MavenPath(name);
                var baseUrl = library["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/";
                url = string.IsNullOrWhiteSpace(name) ? null : MirrorEndpoints.MavenUrl(baseUrl, name);
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            relativePath = relativePath.Replace('\\', '/').TrimStart('/');
            // Some installer-generated entries (notably Forge's patched client jar)
            // intentionally have no download URL but already exist in libraries/.
            url = string.IsNullOrWhiteSpace(url) ? "" : source.Rewrite(url);
            var key = relativePath.ToLowerInvariant();
            if (!seen.Add(key))
            {
                continue;
            }

            var exclude = new List<string>();
            if (library["extract"]?["exclude"] is JsonArray excludeArray)
            {
                exclude.AddRange(excludeArray.Select(n => n?.GetValue<string>()).Where(n => n is not null)!);
            }

            result.Add(new ResolvedLibrary(
                name,
                relativePath,
                url,
                sha1,
                size,
                hasNativeClassifier,
                hasNativeClassifier ? nativeClassifier : GetNameClassifier(name),
                exclude));
        }

        return result;
    }

    public static string? GetNameClassifier(string name)
    {
        var parts = name.Split(':');
        return parts.Length > 3 ? parts[3] : null;
    }

    public static string GetLibraryPath(string gameRoot, ResolvedLibrary library) =>
        Path.Combine(gameRoot, "libraries", library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
