using Pia.Shared.Policy;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Mirrors only the parts of the shared contract the client depends on; the full table is pinned server-side.</summary>
public class ClientPolicyContractTests
{
    [Theory]
    [InlineData("serverUrl")]
    [InlineData("syncEnabled")]
    [InlineData("trustSelfSignedCertificates")]
    public void IsDenied_ForBootstrapKey_ReturnsTrue(string key)
    {
        Assert.True(ClientPolicyContract.IsDenied(key));
        Assert.Contains(key, ClientPolicyContract.DeniedKeys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n ")]
    public void Normalize_ForBlankDocument_ReturnsNull(string? document)
    {
        Assert.Null(ClientPolicyContract.Normalize(document));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("  {}\r\n")]
    public void Normalize_ForEmptyObjectDocument_ReturnsNull(string document)
    {
        Assert.Null(ClientPolicyContract.Normalize(document));
    }

    [Fact]
    public void Normalize_ForRealDocument_TrimsAndKeepsTheRestVerbatim()
    {
        const string document = "{ \"defaults\": { \"uiLanguage\": \"DE\" } }";

        Assert.Equal(document, ClientPolicyContract.Normalize("  " + document + " \r\n"));
    }
}
