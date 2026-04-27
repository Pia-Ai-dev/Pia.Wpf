using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pia.Infrastructure;

namespace Pia.Services.Consent;

/// <summary>
/// Per-session Ed25519 signer for the consent audit log. The private key is generated on
/// first use, encrypted with DPAPI, and persisted next to the log; the public key lives in
/// a sibling <c>manifest.json</c> so a verifier can validate the chain without the secret.
/// </summary>
public sealed class AuditChainSigner
{
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly ECDsa _signingKey;

    public string PublicKeyBase64 { get; }

    private AuditChainSigner(ECDsa key, string publicKeyBase64)
    {
        _signingKey = key;
        PublicKeyBase64 = publicKeyBase64;
    }

    /// <summary>
    /// Loads or creates a signer rooted at <paramref name="manifestPath"/>. The manifest stores
    /// the public key (base64) and the DPAPI-encrypted private key (base64). On first call the
    /// directory must already exist.
    /// </summary>
    public static AuditChainSigner LoadOrCreate(string manifestPath, DpapiHelper dpapi)
    {
        if (File.Exists(manifestPath))
        {
            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pub = root.GetProperty("public_key").GetString()!;
            var encPriv = root.GetProperty("private_key_encrypted").GetString()!;
            var privPem = dpapi.Decrypt(encPriv);
            var ec = ECDsa.Create();
            ec.ImportFromPem(privPem);
            return new AuditChainSigner(ec, pub);
        }

        var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = fresh.ExportSubjectPublicKeyInfoPem();
        var publicBase64 = Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo());
        var privatePem = fresh.ExportECPrivateKeyPem();
        var encrypted = dpapi.Encrypt(privatePem);

        var manifest = new
        {
            public_key = publicBase64,
            public_key_pem = publicPem,
            private_key_encrypted = encrypted,
            algorithm = "ECDSA-P256-SHA256",
            created_at = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, CanonicalJson));
        return new AuditChainSigner(fresh, publicBase64);
    }

    /// <summary>
    /// Returns the SHA-256 hash (hex) of the canonical JSON serialization of an event with its
    /// signature stripped. Two events with identical fields produce the same hash.
    /// </summary>
    public static string HashEventWithoutSignature(AuditEvent evt)
    {
        var stripped = evt with { Signature = null };
        var json = JsonSerializer.Serialize(stripped, CanonicalJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public string Sign(AuditEvent evt)
    {
        var hashHex = HashEventWithoutSignature(evt);
        var signature = _signingKey.SignData(Encoding.UTF8.GetBytes(hashHex), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(AuditEvent evt, string publicKeyBase64)
    {
        if (string.IsNullOrEmpty(evt.Signature)) return false;
        try
        {
            var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            var hashHex = HashEventWithoutSignature(evt);
            return ec.VerifyData(
                Encoding.UTF8.GetBytes(hashHex),
                Convert.FromBase64String(evt.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
