using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pia.Native;

namespace Pia.Services.Updates;

/// <summary>Checks an update package against Authenticode before Velopack may install anything from it.</summary>
public static class UpdatePackageVerifier
{
    private const string OrganizationOid = "2.5.4.10";

    public enum CatalogVerdict
    {
        Verified,
        Missing,
        Untrusted,
        NotListed,
    }

    /// <summary>The O= of a validly signed file's signer; null when unsigned or the chain does not verify.</summary>
    public static string? PublisherOf(string path)
    {
        if (!WinTrust.HasValidSignature(path))
            return null;

        try
        {
            // X509CertificateLoader cannot read an Authenticode signer; this is the only managed API that does.
#pragma warning disable SYSLIB0057
            var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var signer = X509CertificateLoader.LoadCertificate(signed.GetRawCertData());
            return OrganizationOf(signer.SubjectName);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The CA vendor only changes the Issuer; the validated Organization is what stays ours.</summary>
    internal static string? OrganizationOf(X500DistinguishedName name)
        => name.EnumerateRelativeDistinguishedNames()
            .FirstOrDefault(rdn => !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == OrganizationOid)
            ?.GetSingleElementValue();

    /// <summary>Throws unless every .exe and .dll in the package is signed by one of <paramref name="trustedPublishers"/>.</summary>
    public static void VerifyBinaries(string packagePath, IReadOnlyCollection<string> trustedPublishers)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "pia-update-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            using var package = ZipFile.OpenRead(packagePath);
            var binaries = package.Entries.Where(IsBinary).ToList();
            if (binaries.Count == 0)
                throw new InvalidDataException("The update package contains no binaries to verify.");

            foreach (var entry in binaries)
            {
                // A generated name, so an entry path never decides where the extracted file lands.
                var file = Path.Combine(scratch, Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
                entry.ExtractToFile(file);
                var publisher = PublisherOf(file);
                File.Delete(file);

                if (publisher is null || !IsTrusted(publisher, trustedPublishers))
                {
                    throw new InvalidDataException(
                        $"Update package entry '{entry.FullName}' is not signed by a trusted publisher ({publisher ?? "no valid signature"}).");
                }
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether a signed release catalog vouches for the package file byte for byte.</summary>
    public static CatalogVerdict CheckCatalog(string? catalogPath, string packagePath, IReadOnlyCollection<string> trustedPublishers)
    {
        if (catalogPath is null || !File.Exists(catalogPath))
            return CatalogVerdict.Missing;

        if (PublisherOf(catalogPath) is not { } publisher || !IsTrusted(publisher, trustedPublishers))
            return CatalogVerdict.Untrusted;

        string hash;
        using (var stream = File.OpenRead(packagePath))
            hash = Convert.ToHexString(SHA256.HashData(stream));

        return WinTrust.CatalogHasMember(catalogPath, hash) ? CatalogVerdict.Verified : CatalogVerdict.NotListed;
    }

    private static bool IsBinary(ZipArchiveEntry entry)
        => entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           || entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrusted(string publisher, IReadOnlyCollection<string> trustedPublishers)
        => trustedPublishers.Contains(publisher, StringComparer.OrdinalIgnoreCase);
}
