using System.Text;
using MCLneo.Core.Models;

namespace MCLneo.Core.Logs;

public sealed record GameLogFile(string Path, string Name, long Size, DateTimeOffset Modified, bool IsCrash);

public sealed class LogService
{
    public IReadOnlyList<GameLogFile> List(GameInstance instance)
    {
        var files = new List<GameLogFile>();
        AddDirectory(files, instance.LogsDirectory, false);
        AddDirectory(files, Path.Combine(instance.Directory, "crash-reports"), true);
        return files.OrderByDescending(f => f.Modified).ToList();
    }

    private static void AddDirectory(List<GameLogFile> files, string directory, bool crash)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(p => p.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            var info = new FileInfo(path);
            files.Add(new GameLogFile(path, Path.GetFileName(path), info.Length,
                info.LastWriteTime, crash));
        }
    }

    public async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return "文件不存在";
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        const long maxBytes = 4 * 1024 * 1024;
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return start > 0 ? $"[已截断前 {start / 1024} KB]\n" + text : text;
    }
}
