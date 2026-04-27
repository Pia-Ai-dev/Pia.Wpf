using System.Security.Cryptography;
using System.Text;

namespace Pia.Services.Consent;

public sealed record ConsentPrompt(string Id, string Language, string Text, string VersionHash);

public static class ConsentPromptTemplates
{
    public static readonly ConsentPrompt InitialConsentLocalOnlyDe = Build(
        "INITIAL_CONSENT_LOCAL_ONLY", "de",
        "Hallo, ich nutze ein Tool, das unser Gespräch lokal auf meinem Computer aufzeichnet "
        + "und für meine Notizen verarbeitet. Es werden keine Daten an externe Dienste gesendet. "
        + "Sind Sie damit einverstanden? Ein kurzes Ja oder Nein genügt.");

    public static readonly ConsentPrompt ClarificationAmbiguousDe = Build(
        "CLARIFICATION_AMBIGUOUS", "de",
        "Entschuldigung, ich habe Ihre Antwort nicht eindeutig verstanden. "
        + "Sind Sie mit der Aufzeichnung einverstanden – ja oder nein?");

    public static readonly ConsentPrompt RevocationConfirmDe = Build(
        "REVOCATION_CONFIRM", "de",
        "Verstanden, die Aufzeichnung wurde gestoppt und alle Notizen gelöscht.");

    private static ConsentPrompt Build(string id, string lang, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{lang}|{text}"));
        return new ConsentPrompt(id, lang, text, Convert.ToHexString(bytes)[..16]);
    }
}
