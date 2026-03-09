using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pia.Models;
using Pia.Services.Interfaces;
using System.Windows;
using Wpf.Ui.Appearance;

namespace Pia.Services;

public class ThemeService : IThemeService
{
    private readonly ILogger<ThemeService> _logger;

    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ThemeValueName = "AppsUseLightTheme";
    private const string DarkThemePath = "pack://application:,,,/Resources/Themes/Dark.xaml";
    private const string LightThemePath = "pack://application:,,,/Resources/Themes/Light.xaml";

    private AppTheme _currentAppliedTheme = AppTheme.System;
    private ResourceDictionary? _currentCustomTheme;
    private bool _isMonitoring = false;

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    public void ApplyTheme(AppTheme theme)
    {
        _currentAppliedTheme = theme;

        var effectiveTheme = theme;

        if (theme == AppTheme.System)
        {
            effectiveTheme = DetectSystemTheme();
            StartMonitoringSystemTheme();
        }
        else
        {
            StopMonitoringSystemTheme();
        }

        ApplyThemeInternal(effectiveTheme);
    }

    public void StartMonitoringSystemTheme()
    {
        if (_isMonitoring)
            return;

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _isMonitoring = true;
    }

    public void StopMonitoringSystemTheme()
    {
        if (!_isMonitoring)
            return;

        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _isMonitoring = false;
    }

    public AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue(ThemeValueName) is int themeValue)
            {
                return themeValue == 1 ? AppTheme.Light : AppTheme.Dark;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect system theme from registry, defaulting to Dark theme");
        }

        return AppTheme.Dark;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_currentAppliedTheme == AppTheme.System)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var systemTheme = DetectSystemTheme();
                ApplyThemeInternal(systemTheme);
            });
        }
    }

    private void ApplyThemeInternal(AppTheme theme)
    {
        var wpfUiTheme = theme == AppTheme.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(wpfUiTheme);
        ApplyCustomTheme(theme);
    }

    private void ApplyCustomTheme(AppTheme theme)
    {
        var themePath = theme == AppTheme.Light ? LightThemePath : DarkThemePath;

        try
        {
            var newTheme = new ResourceDictionary { Source = new Uri(themePath) };
            var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

            if (_currentCustomTheme is not null)
                mergedDictionaries.Remove(_currentCustomTheme);

            mergedDictionaries.Add(newTheme);
            _currentCustomTheme = newTheme;

            _logger.LogInformation("Applied custom {Theme} theme", theme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply custom {Theme} theme", theme);
        }
    }
}
