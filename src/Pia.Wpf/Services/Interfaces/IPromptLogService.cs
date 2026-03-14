namespace Pia.Services.Interfaces;

public interface IPromptLogService
{
    Task LogAsync(string type, string endpoint, string provider, string payload);
    string GetLogFolderPath();
    Task<long> GetTotalLogSizeAsync();
    Task DeleteLogsOlderThanAsync(int days);
    Task DeleteAllLogsAsync();
}
