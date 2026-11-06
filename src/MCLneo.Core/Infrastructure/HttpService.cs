using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MCLneo.Core.Infrastructure;

public sealed class HttpService : IDisposable
{
    private readonly HttpClient _client;

    public HttpService(string userAgent = "MCLneo/0.1.0 (Minecraft launcher; +https://github.com/mclneo)")
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 16
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public HttpClient Client => _client;

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct = default)
    {
        var text = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var text = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
    }

    public async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct = default, int attempts = 3)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = response.StatusCode;
                response.Dispose();
                if ((int)status is >= 500 and <= 599 && attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw new HttpRequestException($"HTTP {(int)status} {status}：{url}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex;
                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt * attempt), ct).ConfigureAwait(false);
                }
            }
        }

        throw new HttpRequestException($"请求失败：{url}", last);
    }

    public void Dispose() => _client.Dispose();
}
