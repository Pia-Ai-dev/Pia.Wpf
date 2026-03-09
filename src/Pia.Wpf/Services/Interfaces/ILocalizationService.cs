using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ILocalizationService
{
    TargetLanguage CurrentLanguage { get; }
    event EventHandler<TargetLanguage>? LanguageChanged;
    void SetLanguage(TargetLanguage language);
    string this[string key] { get; }
    string Format(string key, params object[] args);
}
