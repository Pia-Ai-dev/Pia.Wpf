using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IThemeService
{
    /// <summary>
    /// Raised on the UI thread after a theme has been fully applied — every dictionary swapped, every token live.
    /// <para>
    /// Exists for one reason: a brush resolved by KEY inside an <see cref="System.Windows.Data.IValueConverter"/>
    /// is a SNAPSHOT. A converter re-runs only when its source value changes, and the swap cannot recolour the
    /// object it already returned (WPF freezes freezables once their dictionary is owned, so neither mutating the
    /// brush nor giving it a <c>DynamicResource</c> colour works — both were measured). So a surface whose colour
    /// comes from a converter has to be told to ask again, and this is the telling.
    /// </para>
    /// <para>
    /// Consumers re-raise <c>PropertyChanged</c> for whatever their brush bindings read from; they must unsubscribe
    /// on disposal, because this service is a singleton and outlives every ViewModel.
    /// </para>
    /// </summary>
    event EventHandler? ThemeChanged;

    void ApplyTheme(AppTheme theme);
    void StartMonitoringSystemTheme();
    void StopMonitoringSystemTheme();
    AppTheme DetectSystemTheme();
}
