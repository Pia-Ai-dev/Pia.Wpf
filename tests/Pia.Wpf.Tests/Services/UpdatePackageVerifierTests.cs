using System.IO;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using Pia.Native;
using Pia.Services.Updates;
using Pia.Tests.TestInfrastructure;
using Xunit;
using static Pia.Services.Updates.UpdatePackageVerifier;

namespace Pia.Tests.Services;

/// <summary>
/// Signed inputs come from the machine itself: Microsoft.Data.Sqlite.dll is Authenticode-signed by Microsoft, and
/// Windows ships hundreds of Microsoft-signed catalogs in CatRoot. Pia.Wpf.dll is the unsigned counterexample.
/// </summary>
public sealed class UpdatePackageVerifierTests : IDisposable
{
    // New-FileCatalog -CatalogVersion 2.0 over two files; unsigned, so only membership can be tested with it.
    private const string CatalogBase64 =
        "MIIDbAYJKoZIhvcNAQcCoIIDXTCCA1kCAQExADCCA04GCSsGAQQBgjcKAaCCAz8wggM7MAwGCisGAQQBgjcMAQEEEJRKJOJV9TdF" +
        "rKadVVNioysXDTI2MTAwMTEyMDUxNVowDgYKKwYBBAGCNwwBAwUAMIIC+DCBiAQUG8DnoYCrlQV7KRkw9L4SnTKglOAxcDAQBgor" +
        "BgEEAYI3DAIDMQKCADBcBgorBgEEAYI3DAIBMU4wTB4QAEYAaQBsAGUAUABhAHQAaAIEEAEAAQQyUABpAGEALgBXAHAAZgAtADkA" +
        "LgA5AC4AOQAtAGYAdQBsAGwALgBuAHUAcABrAGcAAAAwgYoEFCEC5qQ5eAq5gvSsScta+dDo8DkZMXIwEAYKKwYBBAGCNwwCAzEC" +
        "ggAwXgYKKwYBBAGCNwwCATFQME4eEABGAGkAbABlAFAAYQB0AGgCBBABAAEENFAAaQBhAC4AVwBwAGYALQA5AC4AOQAuADkALQBk" +
        "AGUAbAB0AGEALgBuAHUAcABrAGcAAAAwge4EIJtQlAJfDaulBaU63LzSEUB8HNWoDX3ogwECtjHU0IUSMYHJMBAGCisGAQQBgjcM" +
        "AgMxAoIAMFUGCisGAQQBgjcCAQQxRzBFMBAGCisGAQQBgjcCARmiAoAAMDEwDQYJYIZIAWUDBAIBBQAEIJtQlAJfDaulBaU63LzS" +
        "EUB8HNWoDX3ogwECtjHU0IUSMF4GCisGAQQBgjcMAgExUDBOHhAARgBpAGwAZQBQAGEAdABoAgQQAQABBDRQAGkAYQAuAFcAcABm" +
        "AC0AOQAuADkALgA5AC0AZABlAGwAdABhAC4AbgB1AHAAawBnAAAAMIHsBCD8HhqA2IgGSGsIFTm3LIVa2vmT0pcluT62weXo/yk5" +
        "ODGBxzAQBgorBgEEAYI3DAIDMQKCADBVBgorBgEEAYI3AgEEMUcwRTAQBgorBgEEAYI3AgEZogKAADAxMA0GCWCGSAFlAwQCAQUA" +
        "BCD8HhqA2IgGSGsIFTm3LIVa2vmT0pcluT62weXo/yk5ODBcBgorBgEEAYI3DAIBMU4wTB4QAEYAaQBsAGUAUABhAHQAaAIEEAEA" +
        "AQQyUABpAGEALgBXAHAAZgAtADkALgA5AC4AOQAtAGYAdQBsAGwALgBuAHUAcABrAGcAAAAxAA==";

    private const string ListedContentSha256 = "FC1E1A80D88806486B081539B72C855ADAF993D29725B93EB6C1E5E8FF293938";

    private static readonly string[] Microsoft = ["Microsoft Corporation"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PiaUpdateVerify_" + Guid.NewGuid().ToString("N"));

    public UpdatePackageVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TempPath.Remove(_dir);

    private static string SignedDll => typeof(SqliteConnection).Assembly.Location;

    private static string UnsignedDll => typeof(UpdatePackageVerifier).Assembly.Location;

    private static string MicrosoftCatalog
        => Directory.EnumerateFiles(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}"),
                "*.cat")
            .First();

    private string Package(params (string Entry, string Source)[] files)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".nupkg");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("Pia.Wpf.nuspec");
        foreach (var (entry, source) in files)
            zip.CreateEntryFromFile(source, entry);
        return path;
    }

    [Fact]
    public void PublisherOf_reads_the_signers_organization()
        => Assert.Equal("Microsoft Corporation", PublisherOf(SignedDll));

    [Fact]
    public void PublisherOf_an_unsigned_file_is_null()
        => Assert.Null(PublisherOf(UnsignedDll));

    [Theory]
    [InlineData("CN=neo42 GmbH, O=neo42 GmbH, L=Wiehl, C=DE", "neo42 GmbH")]
    [InlineData("CN=neo42 GmbH, O=neo42 GmbH, L=Wiehl, S=Nordrhein-Westfalen, C=DE, SERIALNUMBER=HRB 123", "neo42 GmbH")]
    [InlineData("CN=Someone", null)]
    public void OrganizationOf_ignores_everything_a_new_certificate_vendor_may_change(string subject, string? expected)
        => Assert.Equal(expected, OrganizationOf(new X500DistinguishedName(subject)));

    [Fact]
    public void A_package_whose_binaries_are_signed_by_a_trusted_publisher_passes()
        => VerifyBinaries(Package(("lib/app/Microsoft.Data.Sqlite.dll", SignedDll)), Microsoft);

    [Fact]
    public void A_signed_binary_from_an_untrusted_publisher_fails()
        => Assert.Throws<InvalidDataException>(
            () => VerifyBinaries(Package(("lib/app/Microsoft.Data.Sqlite.dll", SignedDll)), ["neo42 GmbH"]));

    [Fact]
    public void One_unsigned_binary_fails_the_whole_package()
    {
        var package = Package(("lib/app/Microsoft.Data.Sqlite.dll", SignedDll), ("lib/app/Pia.Wpf.dll", UnsignedDll));

        var ex = Assert.Throws<InvalidDataException>(() => VerifyBinaries(package, Microsoft));
        Assert.Contains("lib/app/Pia.Wpf.dll", ex.Message);
    }

    [Fact]
    public void A_package_with_no_binaries_fails()
        => Assert.Throws<InvalidDataException>(() => VerifyBinaries(Package(), Microsoft));

    [Fact]
    public void An_entry_path_never_decides_where_a_binary_is_extracted()
    {
        VerifyBinaries(Package(("../../escaped.dll", SignedDll)), Microsoft);

        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "escaped.dll"));
    }

    [Fact]
    public void A_missing_catalog_is_reported_as_missing()
    {
        var package = Path.Combine(_dir, "p.nupkg");
        File.WriteAllText(package, "x");

        Assert.Equal(CatalogVerdict.Missing, CheckCatalog(null, package, Microsoft));
        Assert.Equal(CatalogVerdict.Missing, CheckCatalog(Path.Combine(_dir, "absent.cat"), package, Microsoft));
    }

    [Fact]
    public void A_catalog_signed_by_an_untrusted_publisher_is_untrusted()
    {
        var package = Path.Combine(_dir, "p.nupkg");
        File.WriteAllText(package, "x");

        Assert.Equal(CatalogVerdict.Untrusted, CheckCatalog(MicrosoftCatalog, package, ["neo42 GmbH"]));
    }

    [Fact]
    public void A_trusted_catalog_that_does_not_list_the_package_says_so()
    {
        var package = Path.Combine(_dir, "p.nupkg");
        File.WriteAllText(package, "not in any catalog " + Guid.NewGuid());

        Assert.Equal(CatalogVerdict.NotListed, CheckCatalog(MicrosoftCatalog, package, Microsoft));
    }

    [Fact]
    public void An_unsigned_catalog_is_untrusted_even_when_it_lists_the_package()
    {
        var catalog = Path.Combine(_dir, "release.cat");
        File.WriteAllBytes(catalog, Convert.FromBase64String(CatalogBase64));
        var package = Path.Combine(_dir, "Pia.Wpf-9.9.9-full.nupkg");
        File.WriteAllText(package, "hello full package");

        Assert.Equal(CatalogVerdict.Untrusted, CheckCatalog(catalog, package, Microsoft));
    }

    [Fact]
    public void Catalog_membership_is_looked_up_by_the_file_hash_tag()
    {
        var catalog = Path.Combine(_dir, "release.cat");
        File.WriteAllBytes(catalog, Convert.FromBase64String(CatalogBase64));

        Assert.True(WinTrust.CatalogHasMember(catalog, ListedContentSha256));
        Assert.True(WinTrust.CatalogHasMember(catalog, ListedContentSha256.ToLowerInvariant()));
        Assert.False(WinTrust.CatalogHasMember(catalog, new string('0', 64)));
    }
}
