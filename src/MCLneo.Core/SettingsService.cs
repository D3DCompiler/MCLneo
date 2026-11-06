using MCLneo.Core.Infrastructure;

namespace MCLneo.Core;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsService(AppPaths paths, AppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public LauncherSettings Current { get; private set; } = new();

    public async Task<LauncherSettings> LoadAsync(CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        try
        {
            var loaded = await PathSafety.ReadJsonAsync<LauncherSettings>(_paths.SettingsFile, ct).ConfigureAwait(false);
            Current = (loaded ?? new LauncherSettings()).Normalize();
        }
        catch (Exception ex)
        {
            _log.Warn("读取设置失败，已恢复默认设置", ex);
            Current = new LauncherSettings().Normalize();
        }

        Directory.CreateDirectory(Current.GameRoot);
        return Current;
    }

    /// <summary>
    /// Synchronous shutdown save. Closing the main window must never block the UI
    /// thread on an async continuation; the settings file is tiny.
    /// </summary>
    public void SaveSynchronously()
    {
        Current.Normalize();
        try
        {
            Directory.CreateDirectory(Current.GameRoot);
            var json = System.Text.Json.JsonSerializer.Serialize(Current, JsonDefaults.Options);
            var temp = _paths.SettingsFile + ".sync-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
            File.Move(temp, _paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            _log.Error("同步保存设置失败", ex);
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        Current.Normalize();
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Current.GameRoot);
            await PathSafety.WriteJsonAtomicAsync(_paths.SettingsFile, Current, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("保存设置失败", ex);
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
