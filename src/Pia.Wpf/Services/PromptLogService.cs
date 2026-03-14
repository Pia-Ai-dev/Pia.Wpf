using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public partial class PromptLogService : IPromptLogService
{
    private static readonly string LogBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "PromptLogs");

    private readonly ISettingsService _settingsService;
    private readonly ILogger<PromptLogService> _logger;

    public PromptLogService(ISettingsService settingsService, ILogger<PromptLogService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task LogAsync(string type, string endpoint, string provider, string payload)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (!settings.Privacy.PromptLoggingEnabled)
                return;

            var now = DateTime.UtcNow;
            var dateFolder = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var directory = Path.Combine(LogBasePath, dateFolder);
            Directory.CreateDirectory(directory);

            var sanitizedType = SanitizeFileName().Replace(type, "-").ToLowerInvariant();
            var timeStamp = now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var baseName = $"{timeStamp}_{sanitizedType}";
            var filePath = GetUniqueFilePath(directory, baseName);

            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {now:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Type: {type}");
            sb.AppendLine($"Endpoint: {endpoint}");
            sb.AppendLine($"Provider: {provider}");
            sb.AppendLine("---");
            sb.AppendLine(payload);

            await File.WriteAllTextAsync(filePath, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write prompt log");
        }
    }

    public string GetLogFolderPath() => LogBasePath;

    public Task<long> GetTotalLogSizeAsync()
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(LogBasePath))
                return 0L;

            return new DirectoryInfo(LogBasePath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        });
    }

    public Task DeleteLogsOlderThanAsync(int days)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(LogBasePath))
                return;

            var cutoff = DateTime.UtcNow.Date.AddDays(-days);
            foreach (var dir in Directory.GetDirectories(LogBasePath))
            {
                var folderName = Path.GetFileName(dir);
                if (DateTime.TryParseExact(folderName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && date < cutoff)
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete prompt log folder: {Folder}", dir);
                    }
                }
            }
        });
    }

    public Task DeleteAllLogsAsync()
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(LogBasePath))
                return;

            try
            {
                Directory.Delete(LogBasePath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete all prompt logs");
            }
        });
    }

    private static string GetUniqueFilePath(string directory, string baseName)
    {
        var filePath = Path.Combine(directory, $"{baseName}.txt");
        if (!File.Exists(filePath))
            return filePath;

        for (var i = 1; i < 1000; i++)
        {
            filePath = Path.Combine(directory, $"{baseName}_{i}.txt");
            if (!File.Exists(filePath))
                return filePath;
        }

        return Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}.txt");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-]")]
    private static partial Regex SanitizeFileName();
}
