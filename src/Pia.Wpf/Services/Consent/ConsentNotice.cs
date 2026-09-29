using System.Collections.ObjectModel;
using Pia.Models;

namespace Pia.Services.Consent;

/// <summary>The notice text people consent against, as the version and purposes a record can cite.</summary>
public static class ConsentNotice
{
    /// <summary>The direct-transcription disclaimer (<c>DirectTrans_Disclaimer_*</c>).</summary>
    public const int Version = 1;

    // SHA-256 of the disclaimer in en/de/fr; ConsentNoticeTests fails when the text changes but Version does not.
    internal const string TextHash = "c5115e14090c17640ab07524add7f0e04ca104ff0c5793d13e74c50d5b014318";

    public static IReadOnlyList<string> Purposes { get; } =
        new ReadOnlyCollection<string>(["transcribe", "store", "summarize"]);

    /// <summary>The Teams host acknowledgement (<c>Routines_Field_MeetingConsent</c>).</summary>
    public const int TeamsVersion = 1;

    internal const string TeamsTextHash = "d521e281482b5dc4a3195fb56b38193571869dc45e9b5f02258595eccab5a54d";

    public static IReadOnlyList<string> TeamsPurposes { get; } = new ReadOnlyCollection<string>(["transcribe", "store"]);

    /// <summary>Two-letter code of the UI language, which is the language the notice was shown in.</summary>
    public static string LanguageOf(TargetLanguage uiLanguage) => uiLanguage switch
    {
        TargetLanguage.DE => "de",
        TargetLanguage.FR => "fr",
        _ => "en",
    };
}
