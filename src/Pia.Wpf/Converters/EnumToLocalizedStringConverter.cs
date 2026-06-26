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
            SttBackend.Whisper => "Enum_SttWhisper",
            SttBackend.Parakeet => "Enum_SttParakeet",
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
            MeetingBrowserSelection.BundledChromium => "Enum_MeetingBrowser_Bundled",
            MeetingBrowserSelection.SystemChrome    => "Enum_MeetingBrowser_Chrome",
            MeetingBrowserSelection.SystemEdge      => "Enum_MeetingBrowser_Edge",
            MeetingBrowserSelection.SystemDefault   => "Enum_MeetingBrowser_Default",
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
