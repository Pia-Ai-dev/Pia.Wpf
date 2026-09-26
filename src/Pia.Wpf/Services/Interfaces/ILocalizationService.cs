using System.Globalization;
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ILocalizationService
{
    TargetLanguage CurrentLanguage { get; }
    /// <summary>The active UI culture, distinct from the thread-local <c>CurrentUICulture</c> it can drift from.</summary>
    CultureInfo Culture { get; }
    event EventHandler<TargetLanguage>? LanguageChanged;
    void SetLanguage(TargetLanguage language);
    string this[string key] { get; }
    string Format(string key, params object[] args);
}
