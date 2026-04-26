using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AutostartService : IAutostartService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pia";

    private readonly ILogger<AutostartService> _logger;

    public AutostartService(ILogger<AutostartService> logger)
    {
        _logger = logger;
    }

    public void Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                _logger.LogWarning("Could not determine executable path for autostart");
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exePath}\"");
            _logger.LogInformation("Autostart enabled with path: {ExePath}", exePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable autostart");
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName);
                _logger.LogInformation("Autostart disabled");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable autostart");
        }
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check autostart status");
            return false;
        }
    }

    /// <summary>
    /// Static helper for Velopack install hook (no DI available).
    /// Always enables autostart on fresh install.
    /// </summary>
    public static void EnableStatic()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exePath}\"");
        }
        catch
        {
            // Swallow — no logger available in Velopack hooks
        }
    }

    /// <summary>
    /// Static helper for Velopack update hook (no DI available).
    /// Only updates the path if autostart is already enabled (respects user preference).
    /// </summary>
    public static void UpdatePathIfEnabled()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
        }
        catch
        {
            // Swallow — no logger available in Velopack hooks
        }
    }

    /// <summary>
    /// Static helper for Velopack uninstall hook (no DI available).
    /// </summary>
    public static void DisableStatic()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName);
            }
        }
        catch
        {
            // Swallow — no logger available in Velopack hooks
        }
    }
}
