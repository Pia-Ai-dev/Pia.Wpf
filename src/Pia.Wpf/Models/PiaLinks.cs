using System.Globalization;

namespace Pia.Models;

/// <summary>Public pages the About view links to; the legal ones are the documents Art. 50 transparency points at.</summary>
public static class PiaLinks
{
    public const string Website = "https://pia-ai.de";
    public const string Imprint = "https://pia-ai.de/impressum.html";
    public const string PrivacyPolicy = "https://pia-ai.de/datenschutz.html";

    /// <summary>The desktop client's guide in the reader's UI language. English is the site root; the
    /// other two sit under a language segment, so this is a lookup and not a prefix on a shared base.</summary>
    public static string Documentation => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "de" => "https://docs.pia-ai.de/de/wpf/",
        "fr" => "https://docs.pia-ai.de/fr/wpf/",
        _ => "https://docs.pia-ai.de/wpf/"
    };

    /// <summary>Interim address for AI-related concerns (AI Act Art. 50 complaint channel) until a dedicated alias exists.</summary>
    public const string AiFeedbackAddress = "entwicklung@neo42.de";
    public const string AiFeedbackMailto = "mailto:" + AiFeedbackAddress + "?subject=Pia%20AI%20feedback";
}
