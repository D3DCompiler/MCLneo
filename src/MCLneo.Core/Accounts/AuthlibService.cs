using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCLneo.Core.Infrastructure;
using MCLneo.Core.Models;

namespace MCLneo.Core.Accounts;

/// <summary>Third-party Yggdrasil/authlib-injector login. Microsoft OAuth is intentionally not implemented.</summary>
public sealed class AuthlibService
{
    private const string InjectorApi = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly AppLog _log;

    public AuthlibService(AppPaths paths, HttpService http, AppLog log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    public static string NormalizeServer(string server)
    {
        var value = server.Trim().TrimEnd('/');
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("认证服务器地址无效，应以 https:// 开头", nameof(server));
        }

        return value;
    }

    public async Task<AuthlibAccount> LoginAsync(
        string server,
        string username,
        string password,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        server = NormalizeServer(server);
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("用户名不能为空");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("密码不能为空");
        }

        status?.Report("正在检查认证服务器…");
        var metaText = await _http.GetStringAsync(server, ct).ConfigureAwait(false);
        var meta = JsonNode.Parse(metaText) as JsonObject;
        if (meta?["meta"]?["implementationType"]?.GetValue<string>() is not { } type ||
            !type.Contains("authlib", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("该地址不是 authlib-injector 兼容的认证服务器");
        }

        var clientToken = Guid.NewGuid().ToString("N");
        var payload = new
        {
            agent = new { name = "Minecraft", version = 1 },
            username,
            password,
            clientToken,
            requestUser = false
        };
        status?.Report("正在登录…");
        using var response = await _http.Client.PostAsJsonAsync(server + "/authserver/authenticate", payload, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseError(body) ?? $"登录失败：HTTP {(int)response.StatusCode}");
        }

        var root = JsonNode.Parse(body) as JsonObject
                   ?? throw new InvalidDataException("认证服务器返回了无效 JSON");
        var accessToken = root["accessToken"]?.GetValue<string>()
                          ?? throw new InvalidDataException("认证服务器没有返回 accessToken");
        var selected = root["selectedProfile"] as JsonObject
                       ?? (root["availableProfiles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (selected is null)
        {
            throw new InvalidOperationException("该账号没有可用的 Minecraft 角色");
        }

        var playerName = selected["name"]?.GetValue<string>()
                         ?? throw new InvalidDataException("认证服务器没有返回角色名");
        var uuid = selected["id"]?.GetValue<string>()
                   ?? throw new InvalidDataException("认证服务器没有返回角色 UUID");
        var account = AuthlibAccountStore.ToAccount(server, playerName, uuid, accessToken, clientToken);
        _log.Info($"第三方登录成功：{playerName} @ {server}");
        status?.Report("登录成功");
        return account;
    }

    public async Task<string> EnsureInjectorAsync(
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        var existing = Directory.Exists(_paths.CacheDirectory)
            ? Directory.EnumerateFiles(_paths.CacheDirectory, "authlib-injector-*.jar")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (existing is not null)
        {
            return existing;
        }

        progress?.Report(new InstallProgress("准备第三方登录", 0.1, "下载 authlib-injector"));
        using var document = await _http.GetJsonAsync(InjectorApi, ct).ConfigureAwait(false);
        var url = document.RootElement.GetProperty("download_url").GetString()
                  ?? throw new InvalidDataException("authlib-injector 下载地址为空");
        var version = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        var target = Path.Combine(_paths.CacheDirectory,
            $"authlib-injector-{version ?? DateTime.Now.ToString("yyyyMMdd")}.jar");
        var http = new HttpClient();
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }
        finally
        {
            http.Dispose();
        }

        progress?.Report(new InstallProgress("第三方登录就绪", 1, "authlib-injector"));
        return target;
    }

    private static string? ParseError(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            return root?["errorMessage"]?.GetValue<string>() ?? root?["error"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
