namespace Pia.Models;

/// <summary>Public pages the About view links to; the legal ones are the documents Art. 50 transparency points at.</summary>
public static class PiaLinks
{
    public const string Website = "https://pia-ai.de";
    public const string Imprint = "https://pia-ai.de/impressum.html";
    public const string PrivacyPolicy = "https://pia-ai.de/datenschutz.html";
    public const string Documentation = "https://docs.pia-ai.de";

    /// <summary>Interim address for AI-related concerns (AI Act Art. 50 complaint channel) until a dedicated alias exists.</summary>
    public const string AiFeedbackAddress = "entwicklung@neo42.de";
    public const string AiFeedbackMailto = "mailto:" + AiFeedbackAddress + "?subject=Pia%20AI%20feedback";
}
