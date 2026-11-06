using MCLneo.Core.Infrastructure;

namespace MCLneo.Core.Accounts;

public sealed class AuthlibAccount
{
    public string Server { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public DateTimeOffset LoggedInAt { get; set; } = DateTimeOffset.Now;

    public string Key => $"{Server.TrimEnd('/')}|{Uuid}";
    public string ServerDisplay => Uri.TryCreate(Server, UriKind.Absolute, out var uri) ? uri.Host : Server;
}

public sealed class AuthlibAccountStore
{
    private readonly AppPaths _paths;

    public AuthlibAccountStore(AppPaths paths)
    {
        _paths = paths;
    }

    public sealed class AccountFile
    {
        public List<AuthlibAccount> Accounts { get; set; } = new();
        public string? ActiveKey { get; set; }
    }

    public async Task<AccountFile> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            return await PathSafety.ReadJsonAsync<AccountFile>(_paths.AccountsFile, ct).ConfigureAwait(false)
                   ?? new AccountFile();
        }
        catch
        {
            return new AccountFile();
        }
    }

    public async Task SaveAsync(AccountFile file, CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        await PathSafety.WriteJsonAtomicAsync(_paths.AccountsFile, file, ct).ConfigureAwait(false);
    }

    public static AuthlibAccount ToAccount(string server, string playerName, string uuid, string accessToken,
        string clientToken) => new()
    {
        Server = server.TrimEnd('/'),
        PlayerName = playerName,
        Uuid = uuid.Replace("-", ""),
        AccessToken = accessToken,
        ClientToken = clientToken,
        LoggedInAt = DateTimeOffset.Now
    };

    public static PlayerProfile ToPlayer(AuthlibAccount account) => new(
        account.PlayerName,
        account.Uuid,
        account.AccessToken,
        "mojang",
        IsOffline: false);
}
