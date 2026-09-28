using System.Globalization;
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ILocalizationService
{
    TargetLanguage CurrentLanguage { get; }
    /// <summary>The culture of the selected UI language; the thread culture can drift from it.</summary>
    CultureInfo Culture { get; }
    event EventHandler<TargetLanguage>? LanguageChanged;
    void SetLanguage(TargetLanguage language);
    string this[string key] { get; }
    string Format(string key, params object[] args);
}
