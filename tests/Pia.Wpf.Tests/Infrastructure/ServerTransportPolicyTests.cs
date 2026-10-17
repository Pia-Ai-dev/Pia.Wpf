using System.Net.Http;
using System.Net.Security;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public sealed class ServerTransportPolicyTests
{
    private const string Server = "https://pia.example.test:8443";

    private static bool Accepts(string requestUrl, SslPolicyErrors errors, bool trust = true, string serverUrl = Server)
    {
        var validator = ServerTransportPolicy.ForServer(trust, serverUrl);
        Assert.NotNull(validator);
        return validator(new HttpRequestMessage(HttpMethod.Get, requestUrl), null, null, errors);
    }

    [Fact]
    public void Trust_off_installs_no_validator()
        => Assert.Null(ServerTransportPolicy.ForServer(false, Server));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://pia.example.test")]
    public void Trust_on_without_an_https_server_installs_no_validator(string? serverUrl)
        => Assert.Null(ServerTransportPolicy.ForServer(true, serverUrl));

    [Fact]
    public void The_configured_server_accepts_an_untrusted_certificate()
        => Assert.True(Accepts("https://pia.example.test:8443/api/sync", SslPolicyErrors.RemoteCertificateChainErrors));

    [Fact]
    public void Host_comparison_ignores_case()
        => Assert.True(Accepts("https://PIA.Example.Test:8443/", SslPolicyErrors.RemoteCertificateNameMismatch));

    [Theory]
    [InlineData("https://storage.example.test/f/plugin.cab")]
    [InlineData("https://pia.example.test/api")]               // same host, other port
    [InlineData("https://pia.example.test.attacker.test:8443/")]
    [InlineData("https://huggingface.co/model.onnx")]
    public void Every_other_host_keeps_full_validation(string requestUrl)
        => Assert.False(Accepts(requestUrl, SslPolicyErrors.RemoteCertificateChainErrors));

    [Fact]
    public void A_valid_certificate_is_accepted_everywhere()
        => Assert.True(Accepts("https://huggingface.co/model.onnx", SslPolicyErrors.None));

    [Fact]
    public void Default_port_matches_an_explicit_443()
        => Assert.True(Accepts("https://pia.example.test:443/", SslPolicyErrors.RemoteCertificateChainErrors,
            serverUrl: "https://pia.example.test"));

    [Theory]
    [InlineData("http://pia.example.test", true)]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("https://pia.example.test", false)]
    [InlineData("http://localhost:5000", false)]
    [InlineData("http://127.0.0.1:5000", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsPlaintextRemote_flags_http_to_a_non_loopback_host(string? serverUrl, bool expected)
        => Assert.Equal(expected, ServerTransportPolicy.IsPlaintextRemote(serverUrl));
}
