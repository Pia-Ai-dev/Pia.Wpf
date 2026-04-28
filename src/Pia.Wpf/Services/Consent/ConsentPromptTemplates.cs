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

    public static readonly ConsentPrompt InitialConsentEuCloudDe = Build(
        "INITIAL_CONSENT_EU_CLOUD", "de",
        "Hallo, ich nutze ein Tool, das unser Gespräch aufzeichnet und für meine Notizen "
        + "verarbeitet. Für die Zusammenfassung wird ein KI-Dienst innerhalb der EU genutzt. "
        + "Personenbezogene Daten werden vor der Übertragung pseudonymisiert. "
        + "Sind Sie damit einverstanden? Ein kurzes Ja oder Nein genügt.");

    public static readonly ConsentPrompt InitialConsentNonEuCloudDe = Build(
        "INITIAL_CONSENT_NON_EU_CLOUD", "de",
        "Hallo, ich nutze ein Tool, das unser Gespräch aufzeichnet und für meine Notizen "
        + "verarbeitet. Für die Zusammenfassung wird ein KI-Dienst außerhalb der EU genutzt "
        + "(Drittland; Übermittlung auf Basis Standardvertragsklauseln). "
        + "Personenbezogene Daten werden vor der Übertragung pseudonymisiert. "
        + "Sind Sie damit ausdrücklich einverstanden? Ein kurzes Ja oder Nein genügt.");

    /// <summary>Selects the appropriate initial-consent prompt for the active profile.</summary>
    public static ConsentPrompt InitialConsentForProfile(SecurityProfile profile) => profile.Mode switch
    {
        Pia.Models.SecurityMode.Strict => InitialConsentLocalOnlyDe,
        Pia.Models.SecurityMode.Standard => InitialConsentEuCloudDe,
        Pia.Models.SecurityMode.Permissive => InitialConsentNonEuCloudDe,
        _ => InitialConsentLocalOnlyDe,
    };

    public static readonly ConsentPrompt ClarificationAmbiguousDe = Build(
        "CLARIFICATION_AMBIGUOUS", "de",
        "Entschuldigung, ich habe Ihre Antwort nicht eindeutig verstanden. "
        + "Sind Sie mit der Aufzeichnung einverstanden – ja oder nein?");

    public static readonly ConsentPrompt RevocationConfirmDe = Build(
        "REVOCATION_CONFIRM", "de",
        "Verstanden, die Aufzeichnung wurde gestoppt und alle Notizen gelöscht.");

    /// <summary>
    /// Biometric opt-in (Phase 5). The default 12-month retention is named explicitly
    /// in the prompt itself, satisfying the spec's transparency requirement that the
    /// retention period must be surfaced in the consent moment.
    /// </summary>
    public static readonly ConsentPrompt BiometricOptInDe = Build(
        "BIOMETRIC_OPT_IN", "de",
        "Möchten Sie, dass ich Ihre Stimme für künftige Gespräche speichere, "
        + "damit ich Sie beim nächsten Mal nicht erneut um Einwilligung bitten muss? "
        + "Die Speicherung erfolgt für zwölf Monate. Diese Speicherung ist freiwillig "
        + "und kann jederzeit widerrufen werden. Sagen Sie bitte Ja oder Nein.");

    public static readonly ConsentPrompt BiometricOptInEn = Build(
        "BIOMETRIC_OPT_IN", "en",
        "Would you like me to remember your voice for future conversations, "
        + "so I won't have to ask for consent again next time? "
        + "Storage lasts twelve months. This is optional and can be revoked at any time. "
        + "Please say yes or no.");

    private static ConsentPrompt Build(string id, string lang, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{lang}|{text}"));
        return new ConsentPrompt(id, lang, text, Convert.ToHexString(bytes)[..16]);
    }
}
