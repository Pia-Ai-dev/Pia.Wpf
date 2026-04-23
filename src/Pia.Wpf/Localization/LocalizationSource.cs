using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Pia.Localization;

/// <summary>
/// Singleton source for XAML string bindings. Owns the active UI culture so
/// lookups are not affected by thread-local <c>CurrentUICulture</c> drift.
/// </summary>
public class LocalizationSource : INotifyPropertyChanged
{
    private static readonly LocalizationSource _instance = new();
    public static LocalizationSource Instance => _instance;

    private readonly ResourceManager[] _resourceManagers;
    private CultureInfo _culture = CultureInfo.InvariantCulture;

    private LocalizationSource()
    {
        _resourceManagers =
        [
            Resources.Strings.CommonStrings.ResourceManager,
            Resources.Strings.ViewStrings.ResourceManager,
            Resources.Strings.MessageStrings.ResourceManager,
            Resources.Strings.OptimizingStrings.ResourceManager,
        ];
    }

    public void SetCulture(CultureInfo culture)
    {
        _culture = culture;
        Refresh();
    }

    public string this[string key]
    {
        get
        {
            foreach (var rm in _resourceManagers)
            {
                var value = rm.GetString(key, _culture);
                if (value is not null)
                    return value;
            }
            return $"[{key}]";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Force all XAML bindings to re-evaluate. "Item[]" is required for indexer
    /// bindings — "" or null only refreshes named properties.
    /// </summary>
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
