using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Logging;
using Pia.Paths;

namespace Pia.Services;

/// <summary>
/// Abstract base class for services that persist data to JSON files.
/// Provides common load/save functionality with caching and error handling.
/// </summary>
/// <typeparam name="T">The type of data to persist</typeparam>
public abstract class JsonPersistenceService<T> where T : class
{
    /// <summary>A property, not a <c>static readonly</c> field: a field would freeze at type load, which can
    /// precede any data-directory override.</summary>
    protected static string SettingsDirectory => PiaPaths.RoamingDataDirectory;

    /// <summary>Overridable so a test can point one store at a directory of its own.</summary>
    protected virtual string DirectoryPath => SettingsDirectory;

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
    protected string FilePath => Path.Combine(DirectoryPath, FileName);

    /// <summary>
    /// Creates the default value when the file doesn't exist or is invalid.
    /// </summary>
    protected abstract T CreateDefault();

    protected virtual ILogger Logger => NullLogger.Instance;

    protected virtual TimeProvider Clock => TimeProvider.System;

    protected async Task<T> LoadAsync(bool saveDefaultIfMissing = false)
    {
        if (_cached is not null)
            return _cached;

        Directory.CreateDirectory(DirectoryPath);

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
        catch (JsonException ex)
        {
            Logger.SensitiveDebug("{File} parse error: {Error}", FileName, ex.Message);
            MoveUnreadableFileAside();
            _cached = CreateDefault();
        }

        return _cached;
    }

    // Keeps the unparseable file out of reach of the next save, which would otherwise overwrite it with defaults.
    private void MoveUnreadableFileAside()
    {
        var stamp = Clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var asidePath = $"{FilePath}.unreadable-{stamp}";
        for (var n = 2; File.Exists(asidePath); n++)
            asidePath = $"{FilePath}.unreadable-{stamp}-{n}";

        try
        {
            File.Move(FilePath, asidePath);
            Logger.LogWarning(
                "{File} could not be parsed; moved aside as {AsideFile}, starting with defaults",
                FileName, Path.GetFileName(asidePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(
                "{File} could not be parsed and could not be moved aside ({ErrorType}); starting with defaults",
                FileName, ex.GetType().Name);
            Logger.SensitiveDebug("Moving {File} aside failed: {Error}", FileName, ex.Message);
        }
    }

    /// <summary>
    /// Saves data to the JSON file and updates the cache.
    /// </summary>
    protected async Task SaveAsync(T data)
    {
        Directory.CreateDirectory(DirectoryPath);
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
