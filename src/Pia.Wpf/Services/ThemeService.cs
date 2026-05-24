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
    private const string PiaTokensDarkPath = "pack://application:,,,/Resources/Theme/PiaTokens.Dark.xaml";
    private const string PiaTokensLightPath = "pack://application:,,,/Resources/Theme/PiaTokens.Light.xaml";

    private AppTheme _currentAppliedTheme = AppTheme.System;
    private ResourceDictionary? _currentCustomTheme;
    private ResourceDictionary? _currentPiaTokens;
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
        ApplyPiaTokens(theme);
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

    private void ApplyPiaTokens(AppTheme theme)
    {
        var tokensPath = theme == AppTheme.Light ? PiaTokensLightPath : PiaTokensDarkPath;

        try
        {
            var newTokens = new ResourceDictionary { Source = new Uri(tokensPath) };
            var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

            // Drop any previously-merged Pia token dictionary so the swap wins on key
            // collisions (e.g. SystemAccentColorPrimaryBrush). Match by source path so the
            // baseline merged from App.xaml is also removed on the first apply.
            for (var i = mergedDictionaries.Count - 1; i >= 0; i--)
            {
                var src = mergedDictionaries[i].Source?.OriginalString;
                if (src != null && src.Contains("PiaTokens", StringComparison.OrdinalIgnoreCase))
                    mergedDictionaries.RemoveAt(i);
            }

            mergedDictionaries.Add(newTokens);
            _currentPiaTokens = newTokens;

            _logger.LogInformation("Applied Pia {Theme} tokens", theme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Pia {Theme} tokens", theme);
        }
    }
}
