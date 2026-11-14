using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace MCLneo.Core.Infrastructure;

public sealed record DownloadItem(
    string Url,
    string Destination,
    string? Sha1 = null,
    long Size = -1,
    string? Label = null,
    string? FallbackUrl = null);

public sealed record DownloadProgress(
    int Completed,
    int Total,
    long BytesReceived,
    long BytesTotal,
    string CurrentFile)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Completed / (double)Total, 0, 1);
}

public sealed class DownloadService
{
    private readonly HttpService _http;
    private readonly AppLog _log;

    public DownloadService(HttpService http, AppLog log)
    {
        _http = http;
        _log = log;
    }

    public async Task DownloadFileAsync(
        DownloadItem item,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        await DownloadOneAsync(item, progress, 1, 1, null, ct).ConfigureAwait(false);
    }

    public async Task DownloadBatchAsync(
        IReadOnlyList<DownloadItem> items,
        IProgress<DownloadProgress>? progress = null,
        int parallelism = 8,
        CancellationToken ct = default)
    {
        var unique = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.Destination))
            .GroupBy(i => Path.GetFullPath(i.Destination), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (unique.Count == 0)
        {
            progress?.Report(new DownloadProgress(0, 0, 0, 0, "没有需要下载的文件"));
            return;
        }

        var completed = 0;
        var totalBytes = unique.Sum(i => Math.Max(0, i.Size));
        var receivedBytes = 0L;
        var gate = new object();
        var lastReport = Stopwatch.StartNew();
        var currentFile = unique[0].Label ?? Path.GetFileName(unique[0].Destination);
        var perFileBytes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void Report(bool force)
        {
            lock (gate)
            {
                if (!force && lastReport.ElapsedMilliseconds < 120)
                {
                    return;
                }

                lastReport.Restart();
                progress?.Report(new DownloadProgress(
                    Volatile.Read(ref completed),
                    unique.Count,
                    receivedBytes,
                    totalBytes,
                    Volatile.Read(ref currentFile)));
            }
        }

        Report(true);

        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(parallelism, 1, 32),
                CancellationToken = ct
            },
            async (item, token) =>
            {
                Volatile.Write(ref currentFile, item.Label ?? Path.GetFileName(item.Destination));
                await DownloadOneAsync(item, null, unique.Count, 0, bytes =>
                {
                    perFileBytes[item.Destination] = bytes;
                    receivedBytes = perFileBytes.Values.Sum();
                    Report(false);
                }, token).ConfigureAwait(false);
                Interlocked.Increment(ref completed);
                Report(true);
            }).ConfigureAwait(false);

        Report(true);
    }

    private async Task DownloadOneAsync(
        DownloadItem item,
        IProgress<DownloadProgress>? progress,
        int total,
        int completedBefore,
        Action<long>? bytesCallback,
        CancellationToken ct)
    {
        var destination = Path.GetFullPath(item.Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (await IsValidExistingAsync(destination, item, ct).ConfigureAwait(false))
        {
            progress?.Report(new DownloadProgress(completedBefore + 1, total, item.Size, item.Size,
                item.Label ?? Path.GetFileName(destination)));
            bytesCallback?.Invoke(Math.Max(0, item.Size));
            return;
        }

        var temp = destination + ".part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var requestUrl = attempt > 1 && !string.IsNullOrWhiteSpace(item.FallbackUrl)
                        ? item.FallbackUrl
                        : item.Url;
                    using var response = await _http.SendWithRetryAsync(requestUrl, ct).ConfigureAwait(false);
                    var expected = response.Content.Headers.ContentLength ?? item.Size;
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    string actualHash;
                    long received;
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
                    {
                        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                         FileShare.None, 128 * 1024,
                                         FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            var buffer = new byte[128 * 1024];
                            received = 0L;
                            var lastTick = Stopwatch.StartNew();
                            int read;
                            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                hash.AppendData(buffer, 0, read);
                                received += read;
                                if (lastTick.ElapsedMilliseconds >= 120)
                                {
                                    lastTick.Restart();
                                    bytesCallback?.Invoke(received);
                                    progress?.Report(new DownloadProgress(completedBefore, total, received, expected,
                                        item.Label ?? Path.GetFileName(destination)));
                                }
                            }

                            await output.FlushAsync(ct).ConfigureAwait(false);
                        }

                        actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    }
                    if (!string.IsNullOrWhiteSpace(item.Sha1) &&
                        !string.Equals(actualHash, item.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"文件校验失败：{Path.GetFileName(destination)}（期望 {item.Sha1}，实际 {actualHash}）");
                    }

                    if (item.Size > 0 && received != item.Size)
                    {
                        throw new InvalidDataException(
                            $"文件大小不符：{Path.GetFileName(destination)}（期望 {item.Size}，实际 {received}）");
                    }

                    File.Move(temp, destination, true);
                    progress?.Report(new DownloadProgress(completedBefore + 1, total, received, received,
                        item.Label ?? Path.GetFileName(destination)));
                    bytesCallback?.Invoke(received);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                {
                    last = ex;
                    TryDelete(temp);
                    if (attempt < 3)
                    {
                        _log.Warn($"下载重试 {attempt}/3：{item.Url}", ex);
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
                    }
                }
            }

            throw new IOException($"下载失败：{item.Url}", last);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<bool> IsValidExistingAsync(string path, DownloadItem item, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (item.Size > 0 && info.Length != item.Size)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.Sha1))
        {
            return true;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, item.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
