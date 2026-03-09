using System.Windows.Data;
using System.Windows.Markup;

namespace Pia.Localization;

/// <summary>
/// XAML markup extension for localized strings.
/// Usage: &lt;TextBlock Text="{loc:Str Optimize_Title}" /&gt;
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public class StrExtension : MarkupExtension
{
    public string Key { get; }

    public StrExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
