using System;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AuthServiceLoginCallbackTests
{
    private const string RedirectUri = "http://localhost:53123/";

    private static NameValueCollection Callback(string query) =>
        HttpUtility.ParseQueryString(new Uri(RedirectUri + query).Query);

    [Fact]
    public void TriageLoginCallback_ProviderError_SurfacesTheServerMessage()
    {
        var query = Callback("?error=access_denied&message=You+cancelled+the+sign-in&state=abc");

        var triage = AuthService.TriageLoginCallback(
            query["error"], query["message"], query["access_token"], query["code"]);

        Assert.Equal(AuthService.LoginCallbackKind.ProviderError, triage.Kind);
        Assert.Equal("You cancelled the sign-in", triage.Failure);
        Assert.Null(triage.Code);
    }

    [Fact]
    public void TriageLoginCallback_ProviderErrorWithoutAMessage_FallsBackToLoginFailed()
    {
        var query = Callback("?error=server_error&state=abc");

        var triage = AuthService.TriageLoginCallback(
            query["error"], query["message"], query["access_token"], query["code"]);

        Assert.Equal(AuthService.LoginCallbackKind.ProviderError, triage.Kind);
        Assert.Equal("Login failed", triage.Failure);
    }

    [Fact]
    public void TriageLoginCallback_TokensInTheUrl_AreRefused()
    {
        var query = Callback("?access_token=eyJhbGciOi&refresh_token=r-9f2&state=abc");

        var triage = AuthService.TriageLoginCallback(
            query["error"], query["message"], query["access_token"], query["code"]);

        Assert.Equal(AuthService.LoginCallbackKind.LegacyTokens, triage.Kind);
        Assert.Null(triage.Code);
        Assert.Equal("Login failed - the server does not support this client's login flow yet", triage.Failure);
    }

    [Fact]
    public void TriageLoginCallback_TokensAlongsideACode_AreStillRefused()
    {
        var query = Callback("?code=abc123&access_token=eyJhbGciOi&state=abc");

        var triage = AuthService.TriageLoginCallback(
            query["error"], query["message"], query["access_token"], query["code"]);

        Assert.Equal(AuthService.LoginCallbackKind.LegacyTokens, triage.Kind);
        Assert.Null(triage.Code);
    }

    [Fact]
    public void TriageLoginCallback_NeitherCodeNorErrorNorTokens_IsRefusedAsNoLoginCode()
    {
        var query = Callback("?state=abc");

        var triage = AuthService.TriageLoginCallback(
            query["error"], query["message"], query["access_token"], query["code"]);

        Assert.Equal(AuthService.LoginCallbackKind.MissingCode, triage.Kind);
        Assert.Null(triage.Code);
        Assert.Equal("Login failed - no login code received", triage.Failure);
    }

    [Fact]
    public void TriageLoginCallback_Code_IsAccepted()
    {
        var query = Callback("?code=abc123&state=abc");

        var triage = AuthService.TriageLoginCallback(
            query["error"], query["message"], query["access_token"], query["code"]);

        Assert.Equal(AuthService.LoginCallbackKind.Code, triage.Kind);
        Assert.Equal("abc123", triage.Code);
        Assert.Null(triage.Failure);
    }

    [Fact]
    public async Task WaitForLoginCallbackAsync_KeepsWaitingUntilThisLoginsStateArrives()
    {
        var state = AuthService.CreateLoginState();
        var port = FreeLoopbackPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var wait = AuthService.WaitForLoginCallbackAsync(listener, state, cts.Token);

        using var client = new HttpClient();
        var forged = client.GetAsync($"{prefix}?code=junk&state=not-the-state-this-login-minted", cts.Token);

        // A rejected callback is answered and the loop goes round again, so the 404 lands first and the wait
        // is still pending. Were it accepted, it would win this race and leave the request unanswered.
        Assert.True(await Task.WhenAny(wait, forged) == forged,
            "a loopback callback carrying a foreign state ended the pending login");
        Assert.Equal(HttpStatusCode.NotFound, (await forged).StatusCode);
        Assert.False(wait.IsCompleted);

        var genuine = client.GetAsync($"{prefix}?code=real&state={Uri.EscapeDataString(state)}", cts.Token);
        var context = await wait;

        Assert.Equal("real", context.Request.QueryString["code"]);
        context.Response.Close();
        await genuine;
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void LoginCallbackStateMatches_CallbackFromThisLogin_EndsTheWait()
    {
        var state = AuthService.CreateLoginState();
        var query = Callback($"?code=abc123&state={Uri.EscapeDataString(state)}");

        Assert.True(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Fact]
    public void LoginCallbackStateMatches_CodeCallbackWithForeignState_IsRejected()
    {
        var state = AuthService.CreateLoginState();
        var query = Callback("?code=junk&state=not-the-state-this-login-minted");

        Assert.False(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Fact]
    public void LoginCallbackStateMatches_ErrorCallbackWithForeignState_IsRejected()
    {
        var state = AuthService.CreateLoginState();
        var query = Callback("?error=access_denied&message=Nope&state=not-the-state-this-login-minted");

        Assert.False(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Fact]
    public void LoginCallbackStateMatches_AccessTokenCallbackWithForeignState_IsRejected()
    {
        var state = AuthService.CreateLoginState();
        var query = Callback("?access_token=eyJhbGciOi&state=not-the-state-this-login-minted");

        Assert.False(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Fact]
    public void LoginCallbackStateMatches_AnotherPendingLoginsState_IsRejected()
    {
        var state = AuthService.CreateLoginState();
        var otherState = AuthService.CreateLoginState();
        var query = Callback($"?code=junk&state={Uri.EscapeDataString(otherState)}");

        Assert.False(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Fact]
    public void LoginCallbackStateMatches_CallbackWithNoStateParameter_IsRejected()
    {
        var state = AuthService.CreateLoginState();
        var query = Callback("?code=junk");

        Assert.Null(query["state"]);
        Assert.False(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Fact]
    public void LoginCallbackStateMatches_StateDifferingOnlyInCase_IsRejected()
    {
        var state = AuthService.CreateLoginState();
        var query = Callback($"?code=junk&state={Uri.EscapeDataString(state.ToUpperInvariant())}");

        Assert.False(AuthService.LoginCallbackStateMatches(state, query["state"]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("", null)]
    public void LoginCallbackStateMatches_LoginWithoutAState_MatchesNothing(string? expected, string? callback)
    {
        Assert.False(AuthService.LoginCallbackStateMatches(expected, callback));
    }

    [Fact]
    public void CreateLoginState_TwoLoginsGetDifferentStates()
    {
        Assert.NotEqual(AuthService.CreateLoginState(), AuthService.CreateLoginState());
    }

    [Fact]
    public void CreateLoginState_Is32BytesOfUnpaddedBase64Url()
    {
        var state = AuthService.CreateLoginState();

        Assert.Equal(43, state.Length);
        Assert.All(state, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }
}
