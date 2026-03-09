using System.Globalization;
using System.Windows.Data;
using Pia.Localization;
using Pia.Models;

namespace Pia.Converters;

public class EnumToLocalizedStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        var key = value switch
        {
            OutputAction.CopyToClipboard => "Enum_CopyToClipboard",
            OutputAction.AutoType => "Enum_AutoType",
            OutputAction.PasteToPreviousWindow => "Enum_PasteToPreviousWindow",
            WhisperModelSize.Tiny => "Enum_WhisperTiny",
            WhisperModelSize.Base => "Enum_WhisperBase",
            WhisperModelSize.Small => "Enum_WhisperSmall",
            WhisperModelSize.Medium => "Enum_WhisperMedium",
            WhisperModelSize.Large => "Enum_WhisperLarge",
            TargetSpeechLanguage.Auto => "Enum_SpeechAuto",
            TargetSpeechLanguage.EN => "Enum_SpeechEN",
            TargetSpeechLanguage.DE => "Enum_SpeechDE",
            TargetSpeechLanguage.FR => "Enum_SpeechFR",
            // Language names always display in their own language
            TargetLanguage.EN => "Enum_LangEN",
            TargetLanguage.DE => "Enum_LangDE",
            TargetLanguage.FR => "Enum_LangFR",
            _ => null
        };

        if (key is not null)
            return LocalizationSource.Instance[key];

        return value.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // All ComboBoxes bind via SelectedItem, so ConvertBack is not needed for display
        return Binding.DoNothing;
    }
}
