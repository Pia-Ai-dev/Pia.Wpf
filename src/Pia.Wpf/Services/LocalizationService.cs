using System.Globalization;
using Microsoft.Extensions.Logging;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class LocalizationService : ILocalizationService
{
    private readonly ILogger<LocalizationService> _logger;
    private TargetLanguage _currentLanguage = TargetLanguage.EN;

    public TargetLanguage CurrentLanguage => _currentLanguage;
    public event EventHandler<TargetLanguage>? LanguageChanged;

    public LocalizationService(ILogger<LocalizationService> logger)
    {
        _logger = logger;
    }

    public string this[string key] => LocalizationSource.Instance[key];

    public string Format(string key, params object[] args)
    {
        var template = LocalizationSource.Instance[key];
        return string.Format(template, args);
    }

    public void SetLanguage(TargetLanguage language)
    {
        if (_currentLanguage == language)
            return;

        _currentLanguage = language;

        var cultureName = language switch
        {
            TargetLanguage.DE => "de",
            TargetLanguage.FR => "fr",
            _ => "en"
        };

        var culture = new CultureInfo(cultureName);
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        LocalizationSource.Instance.Refresh();
        LanguageChanged?.Invoke(this, language);

        _logger.LogInformation("UI language changed to {Language} ({Culture})", language, cultureName);
    }
}
