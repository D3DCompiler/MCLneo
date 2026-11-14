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
            var hasNativeClassifier = VersionRules.TryGetNativeClassifier(library, out var nativeClassifier);
            var classifierNode = hasNativeClassifier
                ? downloads?["classifiers"]?[nativeClassifier] as JsonObject
                : null;

            var exclude = new List<string>();
            if (library["extract"]?["exclude"] is JsonArray excludeArray)
            {
                exclude.AddRange(excludeArray.Select(n => n?.GetValue<string>()).Where(n => n is not null)!);
            }

            void AddEntry(string? path, string? downloadUrl, string? hash, long entrySize,
                bool isNative, string? classifier)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = path.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    downloadUrl = "";
                }
                else
                {
                    downloadUrl = source.Rewrite(downloadUrl);
                }

                var key = path.ToLowerInvariant();
                if (!seen.Add(key)) return;
                result.Add(new ResolvedLibrary(name, path, downloadUrl, hash, entrySize,
                    isNative, classifier, exclude));
            }

            if (classifierNode is not null)
            {
                // A LWJGL-style entry has both a normal artifact and a native
                // classifier. The normal jar is required on the classpath; the
                // classifier jar is only used to populate natives/.
                if (artifact is not null)
                {
                    AddEntry(artifact["path"]?.GetValue<string>(),
                        artifact["url"]?.GetValue<string>(),
                        artifact["sha1"]?.GetValue<string>(),
                        artifact["size"]?.GetValue<long>() ?? -1,
                        false, GetNameClassifier(name));
                }

                AddEntry(classifierNode["path"]?.GetValue<string>(),
                    classifierNode["url"]?.GetValue<string>(),
                    classifierNode["sha1"]?.GetValue<string>(),
                    classifierNode["size"]?.GetValue<long>() ?? -1,
                    true, nativeClassifier);
            }
            else if (artifact is not null)
            {
                AddEntry(artifact["path"]?.GetValue<string>(),
                    artifact["url"]?.GetValue<string>(),
                    artifact["sha1"]?.GetValue<string>(),
                    artifact["size"]?.GetValue<long>() ?? -1,
                    false, GetNameClassifier(name));
            }
            else
            {
                var path = string.IsNullOrWhiteSpace(name) ? null : MirrorEndpoints.MavenPath(name);
                var baseUrl = library["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/";
                var url = string.IsNullOrWhiteSpace(name) ? null : MirrorEndpoints.MavenUrl(baseUrl, name);
                AddEntry(path, url, null, -1, false, GetNameClassifier(name));
            }
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
