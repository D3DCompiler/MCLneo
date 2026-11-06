using System.Text.Json;

namespace MCLneo.Core.Infrastructure;

public static class PathSafety
{
    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    public static string SafeFileName(string name, string fallback = "unnamed")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var chars = name.Trim().Select(c => InvalidNameChars.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim(' ', '.');
        if (result.Length > 96)
        {
            result = result[..96].TrimEnd(' ', '.');
        }

        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    public static string EnsureInside(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);
        if (!candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidateFull, rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"路径越界：{candidateFull}");
        }

        return candidateFull;
    }

    public static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        try
        {
            // Do not use await using here: its implicit DisposeAsync can capture the
            // UI SynchronizationContext when SaveAsync is called from the UI thread.
            await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        File.Move(temp, path, true);
    }

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, ct).ConfigureAwait(false);
    }
}
