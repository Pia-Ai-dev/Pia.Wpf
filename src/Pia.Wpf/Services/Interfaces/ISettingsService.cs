using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ISettingsService
{
    event EventHandler<AppSettings>? SettingsChanged;

    Task<AppSettings> GetSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
    Task SaveDraftAsync(string? draftText);
    Task<string?> GetDraftAsync();
}
