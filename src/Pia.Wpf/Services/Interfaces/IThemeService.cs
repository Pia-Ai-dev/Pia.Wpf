using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IThemeService
{
    void ApplyTheme(AppTheme theme);
    void StartMonitoringSystemTheme();
    void StopMonitoringSystemTheme();
    AppTheme DetectSystemTheme();
}
