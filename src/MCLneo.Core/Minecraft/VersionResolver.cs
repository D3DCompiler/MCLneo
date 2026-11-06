using System.Text.Json.Nodes;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Minecraft;

/// <summary>Resolves inheritsFrom chains into one launchable version document.</summary>
public static class VersionResolver
{
    public static async Task<JsonObject> LoadEffectiveAsync(GameInstance instance, CancellationToken ct = default)
    {
        var current = await ReadVersionAsync(instance.VersionJsonPath, ct).ConfigureAwait(false);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { instance.Id };
        var versionsRoot = Directory.GetParent(instance.Directory)?.FullName
                           ?? throw new InvalidOperationException("无法定位 versions 目录");

        var parentId = current["inheritsFrom"]?.GetValue<string>();
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!visited.Add(parentId))
            {
                throw new InvalidDataException($"版本继承出现循环：{parentId}");
            }

            var parentPath = Path.Combine(versionsRoot, parentId, parentId + ".json");
            if (!File.Exists(parentPath))
            {
                throw new FileNotFoundException(
                    $"缺少父版本 {parentId} 的 JSON。请重新安装基础版本。", parentPath);
            }

            var parent = await ReadVersionAsync(parentPath, ct).ConfigureAwait(false);
            current = Merge(parent, current);
            parentId = parent["inheritsFrom"]?.GetValue<string>();
        }

        return current;
    }

    public static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var merged = (JsonObject)parent.DeepClone();
        foreach (var (key, value) in child.ToList())
        {
            switch (key)
            {
                case "libraries":
                    merged["libraries"] = MergeLibraries(parent["libraries"] as JsonArray, value as JsonArray);
                    break;
                case "arguments":
                    merged["arguments"] = MergeArguments(parent["arguments"] as JsonObject, value as JsonObject);
                    break;
                case "inheritsFrom":
                    merged.Remove(key);
                    break;
                default:
                    merged[key] = value?.DeepClone();
                    break;
            }
        }

        return merged;
    }

    private static JsonArray MergeLibraries(JsonArray? parent, JsonArray? child)
    {
        var result = new List<JsonObject>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(JsonObject? library)
        {
            if (library is null)
            {
                return;
            }

            var key = LibraryKey(library);
            var clone = (JsonObject)library.DeepClone();
            if (index.TryGetValue(key, out var existing))
            {
                result[existing] = clone;
            }
            else
            {
                index[key] = result.Count;
                result.Add(clone);
            }
        }

        foreach (var library in parent?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            Add(library);
        }

        foreach (var library in child?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            Add(library);
        }

        return new JsonArray(result.Select(l => (JsonNode)l).ToArray());
    }

    private static string LibraryKey(JsonObject library)
    {
        var name = library["name"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
        var parts = name.Split(':');
        return parts.Length >= 2
            ? $"{parts[0]}:{parts[1]}:{(parts.Length > 3 ? parts[3] : "")}"
            : name;
    }

    private static JsonObject MergeArguments(JsonObject? parent, JsonObject? child)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "game", "jvm" })
        {
            var array = new JsonArray();
            foreach (var node in parent?[key] as JsonArray ?? new JsonArray())
            {
                array.Add(node?.DeepClone());
            }

            foreach (var node in child?[key] as JsonArray ?? new JsonArray())
            {
                array.Add(node?.DeepClone());
            }

            result[key] = array;
        }

        return result;
    }

    private static async Task<JsonObject> ReadVersionAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("版本 JSON 不存在", path);
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException($"版本 JSON 格式无效：{path}");
    }
}
