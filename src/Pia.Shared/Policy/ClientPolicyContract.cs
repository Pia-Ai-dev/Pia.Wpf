using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pia.Shared.Policy;

/// <summary>
/// Shape rules for the enterprise-policy document, shared by the server's admin write path and the client's
/// merge so both refuse the same input. Deliberately validates shape and not vocabulary: the settings schema
/// lives in the client, and a second copy here would drift.
/// </summary>
public static class ClientPolicyContract
{
    public const string DefaultsSection = "defaults";
    public const string EnforceSection = "enforce";

    /// <summary>A document meaning "this group has no policy".</summary>
    public const string EmptyDocument = "{}";

    public const int MaxDocumentBytes = 64 * 1024;

    public static readonly IReadOnlySet<string> Sections =
        new HashSet<string>(StringComparer.Ordinal) { DefaultsSection, EnforceSection };

    /// <summary>
    /// Keys the server may never manage. The first three would let one bad save disconnect a whole group
    /// with no way back — server policy outranks the device file, and these are what the client needs to
    /// reach the server at all. The rest are cursors, credentials and migration markers the client owns.
    /// </summary>
    public static readonly IReadOnlySet<string> DeniedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serverUrl",
            "syncEnabled",
            "trustSelfSignedCertificates",

            "encryptedAccessToken",
            "encryptedRefreshToken",
            "syncUserId",
            "syncUserEmail",
            "syncUserDisplayName",
            "syncProvider",
            "syncDeviceId",
            "lastSyncTimestamp",
            "lastPullETag",
            "lastChatPullETag",
            "lastPushedSettingsHash",
            "lastCatalogVersion",
            "managedPersonaStoreInitialized",
            "clientPolicyInitialized",
            "assistantChatsBackfilledAt",
            "isE2EEEnabled",
            "e2eeEncryptedUmk",
            "e2eeDeviceId",
            "e2eeUmkVersion",
            "e2eeRecoveryConfigured",
            "vaultVersion",
            "ingestSchemaVersion",
            "assistantFolderLayoutVersion",
            "draftText",
            "windowWidth",
            "windowHeight",
            "windowLeft",
            "windowTop"
        };

    public static bool IsDenied(string key) => DeniedKeys.Contains(key);

    /// <summary>Blank is valid and means no policy; use <see cref="Normalize"/> to get the storable form.</summary>
    public static string? Normalize(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
            return null;

        var trimmed = document.Trim();
        return trimmed == EmptyDocument ? null : trimmed;
    }

    public static bool TryValidate(string? document, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(document))
            return true;

        if (Encoding.UTF8.GetByteCount(document) > MaxDocumentBytes)
        {
            error = $"Policy document exceeds {MaxDocumentBytes / 1024} KB.";
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(document);
        }
        catch (JsonException ex)
        {
            error = $"Policy document is not valid JSON: {ex.Message}";
            return false;
        }

        if (root is not JsonObject obj)
        {
            error = "Policy document must be a JSON object.";
            return false;
        }

        foreach (var (name, value) in obj)
        {
            if (!Sections.Contains(name))
            {
                error = $"Unknown section '{name}'. Only '{DefaultsSection}' and '{EnforceSection}' are allowed, in lower case.";
                return false;
            }

            if (value is not JsonObject section)
            {
                error = $"Section '{name}' must be a JSON object.";
                return false;
            }

            foreach (var (key, _) in section)
            {
                if (IsDenied(key))
                {
                    error = $"'{key}' cannot be managed from the server; set it in the device's policy.json instead.";
                    return false;
                }
            }
        }

        return true;
    }
}
