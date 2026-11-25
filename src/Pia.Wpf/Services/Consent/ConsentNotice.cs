using System.Collections.ObjectModel;
using Pia.Models;

namespace Pia.Services.Consent;

/// <summary>The notice text people consent against, as the version and purposes a record can cite.</summary>
public static class ConsentNotice
{
    /// <summary>The direct-transcription disclaimer (<c>DirectTrans_Disclaimer_*</c>).</summary>
    public const int Version = 2;

    // SHA-256 of the disclaimer in en/de/fr; ConsentNoticeTests fails when the text changes but Version does not.
    internal const string TextHash = "6efc9959249947c4b4b497a586875601381f84165bbc9df90a1a6dfe63a9908d";

    public static IReadOnlyList<string> Purposes { get; } =
        new ReadOnlyCollection<string>(["transcribe", "store", "summarize"]);

    /// <summary>The Teams host acknowledgement (<c>Routines_Field_MeetingConsent</c>).</summary>
    public const int TeamsVersion = 2;

    internal const string TeamsTextHash = "58c855723a44f28393299bfc7e6c34fc00647665448e8b32f35f7502d84c5c18";

    public static IReadOnlyList<string> TeamsPurposes { get; } = new ReadOnlyCollection<string>(["transcribe", "store"]);

    /// <summary>The interactive Teams overlay's purposes line and the confirmation beneath it.</summary>
    public const int TeamsLiveVersion = 1;

    internal const string TeamsLiveTextHash = "8128a407a3664e284a0dc25019c572a0dc47eb0fe29a4f2afb27ab86bdc886a8";

    public static IReadOnlyList<string> TeamsLivePurposes { get; } =
        new ReadOnlyCollection<string>(["transcribe", "store", "summarize"]);

    /// <summary>Two-letter code of the UI language, which is the language the notice was shown in.</summary>
    public static string LanguageOf(TargetLanguage uiLanguage) => uiLanguage switch
    {
        TargetLanguage.DE => "de",
        TargetLanguage.FR => "fr",
        _ => "en",
    };
}
