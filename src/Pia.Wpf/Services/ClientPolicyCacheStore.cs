using System.IO;
using System.Text.Json;
using Pia.Models;

namespace Pia.Services;

public class ClientPolicyCacheStore : JsonPersistenceService<CachedClientPolicy>
{
    private readonly string? _directoryOverride;

    public ClientPolicyCacheStore(string? directoryOverride = null)
    {
        _directoryOverride = directoryOverride;
    }

    protected override string FileName => "policy-cache.json";

    protected override string DirectoryPath => _directoryOverride ?? SettingsDirectory;

    protected override CachedClientPolicy CreateDefault() => new();

    public Task<CachedClientPolicy> GetAsync() => LoadAsync();

    public Task SetAsync(CachedClientPolicy data) => SaveAsync(data);

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }

        ClearCache();
    }

    /// <summary>Writes synchronously because the caller cannot await: a lost write would re-apply an
    /// administrator's default over a value the user had already changed.</summary>
    public void SaveNow(CachedClientPolicy data)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, JsonOptions));
        SetCached(data);
    }
}
