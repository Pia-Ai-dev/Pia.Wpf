using System.IO;
using System.Text.Json;

namespace Pia.Services;

/// <summary>
/// Abstract base class for services that persist data to JSON files.
/// Provides common load/save functionality with caching and error handling.
/// </summary>
/// <typeparam name="T">The type of data to persist</typeparam>
public abstract class JsonPersistenceService<T> where T : class
{
    protected static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pia");

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private T? _cached;

    /// <summary>
    /// Gets the filename for the JSON file (without path).
    /// </summary>
    protected abstract string FileName { get; }

    /// <summary>
    /// Gets the full path to the JSON file.
    /// </summary>
    protected string FilePath => Path.Combine(SettingsDirectory, FileName);

    /// <summary>
    /// Creates the default value when the file doesn't exist or is invalid.
    /// </summary>
    protected abstract T CreateDefault();

    /// <summary>
    /// Loads data from the JSON file with caching.
    /// </summary>
    /// <param name="saveDefaultIfMissing">If true, saves the default value when the file doesn't exist.</param>
    protected async Task<T> LoadAsync(bool saveDefaultIfMissing = false)
    {
        if (_cached is not null)
            return _cached;

        Directory.CreateDirectory(SettingsDirectory);

        if (!File.Exists(FilePath))
        {
            _cached = CreateDefault();
            if (saveDefaultIfMissing)
            {
                await SaveAsync(_cached);
            }
            return _cached;
        }

        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            _cached = JsonSerializer.Deserialize<T>(json, JsonOptions) ?? CreateDefault();
        }
        catch (JsonException)
        {
            _cached = CreateDefault();
        }

        return _cached;
    }

    /// <summary>
    /// Saves data to the JSON file and updates the cache.
    /// </summary>
    protected async Task SaveAsync(T data)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(FilePath, json);
        _cached = data;
    }

    /// <summary>
    /// Gets the cached data without loading from file.
    /// Returns null if data hasn't been loaded yet.
    /// </summary>
    protected T? GetCached() => _cached;

    /// <summary>
    /// Sets the cached data directly.
    /// Useful when data is modified in-memory and needs to be saved later.
    /// </summary>
    protected void SetCached(T data) => _cached = data;

    /// <summary>
    /// Clears the cache, forcing a reload on next access.
    /// </summary>
    protected void ClearCache() => _cached = null;
}
