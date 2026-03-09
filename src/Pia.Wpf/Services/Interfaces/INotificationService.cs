namespace Pia.Services.Interfaces;

public interface INotificationService
{
    void ShowToast(string message, int durationMs = 3000);
    void ShowError(string message, int durationMs = 5000);
    void ShowSuccess(string message, int durationMs = 3000);
}
