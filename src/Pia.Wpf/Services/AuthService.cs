using System.Buffers.Text;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Auth;

namespace Pia.Services;

public class AuthService : IAuthService
{
    private readonly ISettingsService _settingsService;
    private readonly DpapiHelper _dpapiHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<AuthService> _logger;
    private readonly IPolicyService? _policyService;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Task _loadStoredTokensTask;

    // Tokens are held DPAPI-encrypted in memory and only decrypted transiently when needed,
    // so a plaintext token never lives in a long-lived field (or a memory dump).
    private string? _encryptedAccessToken;
    private string? _encryptedRefreshToken;
    private DateTime _accessTokenExpiry;

    private string? DecryptAccessToken() => DecryptToken(_encryptedAccessToken);
    private string? DecryptRefreshToken() => DecryptToken(_encryptedRefreshToken);

    private string? DecryptToken(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return null;
        var value = _dpapiHelper.Decrypt(encrypted);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public bool IsLoggedIn { get; private set; }
    public string? UserDisplayName { get; private set; }
    public string? UserEmail { get; private set; }
    public string? Provider { get; private set; }
    public bool RequiresBusinessProfile { get; private set; }

    public event EventHandler<bool>? LoginStateChanged;

    public AuthService(
        ISettingsService settingsService,
        DpapiHelper dpapiHelper,
        IHttpClientFactory httpClientFactory,
        ILocalizationService localizationService,
        ILogger<AuthService> logger,
        IPolicyService? policyService = null)
    {
        _settingsService = settingsService;
        _dpapiHelper = dpapiHelper;
        _httpClientFactory = httpClientFactory;
        _localizationService = localizationService;
        _logger = logger;
        _policyService = policyService;

        _loadStoredTokensTask = LoadStoredTokensAsync();
    }

    private async Task LoadStoredTokensAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.EncryptedRefreshToken))
                return;

            // Keep the refresh token in its already-encrypted form; no plaintext at rest.
            // The persisted access token is intentionally not loaded — it is almost certainly
            // expired by the time the app restarts, so the first GetAccessTokenAsync refreshes it.
            _encryptedRefreshToken = settings.EncryptedRefreshToken;
            UserDisplayName = settings.SyncUserDisplayName;
            UserEmail = settings.SyncUserEmail;
            Provider = settings.SyncProvider;

            if (!string.IsNullOrEmpty(_encryptedRefreshToken))
            {
                IsLoggedIn = true;
                LoginStateChanged?.Invoke(this, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stored auth tokens");
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> LoginAsync(string provider)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl))
            {
                _logger.LogWarning("Server URL not configured");
                return (false, "Server URL not configured");
            }

            var reportMetadata = ShouldReportDeviceMetadata(settings);
            var port = GetRandomPort();
            var redirectUri = $"http://localhost:{port}/";
            var (codeVerifier, codeChallenge) = PkceCodes.Create();
            var state = CreateLoginState();

            // Listen before the browser opens, so the callback can never arrive at an unbound port.
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();
            _logger.LogInformation("OAuth listener started on {Url}", SafeUrl.Format(redirectUri));

            // The server answers with a one-time code, never tokens; only this process holds the verifier.
            var loginUrl = $"{serverUrl}/auth/login?provider={provider}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&code_challenge={codeChallenge}"
                + "&code_challenge_method=S256"
                + $"&state={Uri.EscapeDataString(state)}";
            Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var context = await WaitForLoginCallbackAsync(listener, state, cts.Token);
            _logger.LogInformation("OAuth callback received");

            var query = context.Request.QueryString;
            var error = query["error"];
            var errorMessage = query["message"];
            var triage = TriageLoginCallback(error, errorMessage, query["access_token"], query["code"]);

            LocalLoginResponse? login = null;
            var failure = triage.Failure;
            switch (triage.Kind)
            {
                case LoginCallbackKind.ProviderError:
                    _logger.LogWarning("OAuth callback returned error: {Error} - {Message}", error, errorMessage);
                    break;
                case LoginCallbackKind.LegacyTokens:
                    _logger.LogWarning("OAuth callback carried tokens in the URL; the server predates the code exchange");
                    break;
                case LoginCallbackKind.MissingCode:
                    _logger.LogWarning("OAuth callback carried no login code");
                    break;
                case LoginCallbackKind.Code:
                {
                    // Its own budget: the user may have spent almost all of the callback's five minutes signing in.
                    using var exchangeCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    (login, failure) = await ExchangeLoginCodeAsync(
                        serverUrl, triage.Code!, codeVerifier, settings, reportMetadata, exchangeCts.Token);
                    break;
                }
            }

            if (login is not null)
            {
                try
                {
                    await ApplyLoginAsync(login, provider, settings, reportMetadata);
                }
                catch (Exception ex)
                {
                    // Stored before the page is painted, so "All set" can never sit above a login that was lost.
                    _logger.LogError(ex, "Storing the login failed");
                    login = null;
                    failure = "Login failed";
                }
            }

            try
            {
                await WriteBrowserResponseAsync(context,
                    login is not null
                        ? BuildLoginSuccessHtml(login.User.DisplayName)
                        : BuildLoginErrorHtml(failure ?? "Login failed"),
                    success: login is not null);
            }
            catch (Exception ex)
            {
                // A browser tab that is already gone must not cost the user a login that already succeeded.
                _logger.LogWarning(ex, "Failed to write the OAuth browser response");
            }

            return login is null ? (false, failure ?? "Login failed") : (true, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OAuth login timed out");
            return (false, "Login timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth login failed");
            return (false, "Login failed");
        }
    }

    // A request without this login's state, or carrying neither a code, an error nor legacy tokens, is noise on
    // the port (a probe, a favicon fetch) and must not end the wait: any local page could otherwise decide it.
    internal static async Task<HttpListenerContext> WaitForLoginCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var query = context.Request.QueryString;
            if (LoginCallbackStateMatches(expectedState, query["state"])
                && (!string.IsNullOrEmpty(query["code"])
                    || !string.IsNullOrEmpty(query["error"])
                    || !string.IsNullOrEmpty(query["access_token"])))
                return context;

            context.Response.StatusCode = 404;
            context.Response.KeepAlive = false;
            context.Response.Close();
        }
    }

    internal static string CreateLoginState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    internal static bool LoginCallbackStateMatches(string? expectedState, string? callbackState) =>
        !string.IsNullOrEmpty(expectedState) && string.Equals(expectedState, callbackState, StringComparison.Ordinal);

    internal enum LoginCallbackKind
    {
        ProviderError,
        LegacyTokens,
        MissingCode,
        Code
    }

    internal readonly record struct LoginCallbackTriage(LoginCallbackKind Kind, string? Code, string? Failure);

    internal static LoginCallbackTriage TriageLoginCallback(
        string? error, string? errorMessage, string? accessToken, string? code)
    {
        if (!string.IsNullOrEmpty(error))
            return new LoginCallbackTriage(LoginCallbackKind.ProviderError, null, errorMessage ?? "Login failed");

        // Tokens in a query string are never taken: that a URL cannot log this client in is the point of the exchange.
        if (!string.IsNullOrEmpty(accessToken))
            return new LoginCallbackTriage(LoginCallbackKind.LegacyTokens, null,
                "Login failed - the server does not support this client's login flow yet");

        if (string.IsNullOrEmpty(code))
            return new LoginCallbackTriage(LoginCallbackKind.MissingCode, null, "Login failed - no login code received");

        return new LoginCallbackTriage(LoginCallbackKind.Code, code, null);
    }

    // Failures come back as a result, not an exception, so the browser still gets its error page.
    private async Task<(LocalLoginResponse? Login, string? Error)> ExchangeLoginCodeAsync(
        string serverUrl, string code, string codeVerifier, AppSettings settings, bool reportMetadata,
        CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            if (reportMetadata)
                AttachDeviceMetadata(client, settings);

            var response = await client.PostAsJsonAsync($"{serverUrl}/auth/token", new { code, codeVerifier }, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Login code exchange failed with status {Status}", response.StatusCode);
                return (null, await ReadErrorMessageAsync(response, ct) ?? "Login failed");
            }

            var login = await response.Content.ReadFromJsonAsync<LocalLoginResponse>(ct);
            return login is null ? (null, "Invalid server response") : (login, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The exchange owns this token, so its expiry is a result like any other — letting it throw
            // would skip the browser page this method exists to guarantee.
            _logger.LogWarning("Login code exchange timed out");
            return (null, "Login timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login code exchange failed");
            return (null, "Login failed");
        }
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // Force Connection: close, flush and close the stream explicitly, and leave the listener's teardown to
    // the caller's scope exit — otherwise the browser sees an RST mid-body instead of the whole page.
    private async Task WriteBrowserResponseAsync(HttpListenerContext context, string html, bool success)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = success ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.KeepAlive = false;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes);
        await context.Response.OutputStream.FlushAsync();
        context.Response.OutputStream.Close();
        context.Response.Close();
        _logger.LogInformation("OAuth response written ({Bytes} bytes)", bytes.Length);
    }

    private async Task ApplyLoginAsync(
        LocalLoginResponse login, string provider, AppSettings settings, bool reportedMetadata)
    {
        _encryptedAccessToken = _dpapiHelper.Encrypt(login.AccessToken);
        _encryptedRefreshToken = _dpapiHelper.Encrypt(login.RefreshToken);
        _accessTokenExpiry = AccessTokenExpiryFrom(login.ExpiresIn, DateTime.UtcNow);
        UserDisplayName = login.User.DisplayName;
        UserEmail = login.User.Email;
        Provider = provider;
        RequiresBusinessProfile = login.User.RequiresBusinessProfile;
        IsLoggedIn = true;

        settings.SyncEnabled = true;
        settings.EncryptedAccessToken = _encryptedAccessToken;
        settings.EncryptedRefreshToken = _encryptedRefreshToken;
        settings.SyncUserId = login.User.Id.ToString();
        settings.SyncUserEmail = login.User.Email;
        settings.SyncUserDisplayName = login.User.DisplayName;
        settings.SyncProvider = provider;
        settings.SyncDeviceId ??= Guid.NewGuid().ToString();
        if (reportedMetadata)
            settings.ReportedDeviceMetadata = DeviceMetadataFingerprint();
        await _settingsService.SaveSettingsAsync(settings);

        LoginStateChanged?.Invoke(this, true);
    }

    internal const string DeviceIdHeader = "X-Pia-Device-Id";
    internal const string AppVersionHeader = "X-Pia-App-Version";
    internal const string OsVersionHeader = "X-Pia-OS-Version";

    internal static string DeviceMetadataFingerprint() =>
        $"{AppVersionInfo.FileVersion}|{Environment.OSVersion}";

    // The server only ever learns a device's app/OS version at E2EE registration, so it goes stale the
    // moment the client updates. Token requests carry it instead — but only while it differs from what
    // the server last accepted, so the steady-state refresh stays byte-for-byte what it was.
    internal static bool ShouldReportDeviceMetadata(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.SyncDeviceId)
        && !string.Equals(settings.ReportedDeviceMetadata, DeviceMetadataFingerprint(), StringComparison.Ordinal);

    private static void AttachDeviceMetadata(HttpClient client, AppSettings settings)
    {
        client.DefaultRequestHeaders.Add(DeviceIdHeader, settings.SyncDeviceId);
        client.DefaultRequestHeaders.Add(AppVersionHeader, AppVersionInfo.FileVersion);
        client.DefaultRequestHeaders.Add(OsVersionHeader, Environment.OSVersion.ToString());
    }

    // The server states the lifetime; the fixed 14 minutes only covers a response that omits it.
    internal static DateTime AccessTokenExpiryFrom(int expiresInSeconds, DateTime now)
    {
        if (expiresInSeconds <= 0)
            return now.AddMinutes(14);

        // Refresh a little early, so a token that passes the check here is not expired on arrival.
        var margin = Math.Min(60, expiresInSeconds / 2);
        return now.AddSeconds(expiresInSeconds - margin);
    }

    public async Task<(bool Success, string? ErrorMessage)> LoginWithPasswordAsync(string email, string password)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl))
            {
                _logger.LogWarning("Server URL not configured");
                return (false, "Server URL not configured");
            }

            var reportMetadata = ShouldReportDeviceMetadata(settings);
            using var client = _httpClientFactory.CreateClient();
            if (reportMetadata)
                AttachDeviceMetadata(client, settings);

            var response = await client.PostAsJsonAsync($"{serverUrl}/auth/login/local",
                new LocalLoginRequest { Email = email, Password = password });

            if (!response.IsSuccessStatusCode)
                return (false, await ReadErrorMessageAsync(response, CancellationToken.None) ?? "Login failed");

            var loginResponse = await response.Content.ReadFromJsonAsync<LocalLoginResponse>();
            if (loginResponse is null)
                return (false, "Invalid server response");

            await ApplyLoginAsync(loginResponse, "local", settings, reportMetadata);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local login failed");
            return (false, "Connection error. Please check the server URL.");
        }
    }

    // /auth/me is read inline rather than through Pia.Shared: it carries far more than the one flag.
    public async Task<bool?> RequiresBusinessProfileAsync()
    {
        try
        {
            var serverUrl = (await _settingsService.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
            var token = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(token))
                return null;

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{serverUrl}/auth/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("requiresBusinessProfile", out var flag))
                return null;

            return flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the business-profile state");
            return null;
        }
    }

    public async Task<(bool Success, string? ErrorMessage)> SubmitBusinessProfileAsync(string companyName)
    {
        try
        {
            var serverUrl = (await _settingsService.GetSettingsAsync()).ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl))
                return (false, _localizationService["Sync_LocalAuth_ServerUrlRequired"]);

            var token = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
                return (false, _localizationService["Sync_LocalAuth_SignInRequired"]);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/auth/business-profile")
            {
                Content = JsonContent.Create(new BusinessProfileRequest
                {
                    CompanyName = companyName,
                    ActingAsBusiness = true
                })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                RequiresBusinessProfile = false;
                return (true, null);
            }

            return (false, await ReadErrorMessageAsync(response, CancellationToken.None)
                ?? _localizationService["Sync_Cloud_BusinessProfile_Error"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Submitting the business profile failed");
            return (false, _localizationService["Sync_Cloud_BusinessProfile_Error"]);
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');

            // Revoke refresh token on server
            var refreshToken = DecryptRefreshToken();
            if (!string.IsNullOrEmpty(serverUrl) && !string.IsNullOrEmpty(refreshToken))
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    await client.PostAsJsonAsync($"{serverUrl}/auth/logout",
                        new { refreshToken });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to revoke refresh token on server");
                }
            }

            // Clear local state
            _encryptedAccessToken = null;
            _encryptedRefreshToken = null;
            UserDisplayName = null;
            UserEmail = null;
            Provider = null;
            RequiresBusinessProfile = false;
            IsLoggedIn = false;

            settings.SyncEnabled = false;
            settings.EncryptedAccessToken = null;
            settings.EncryptedRefreshToken = null;
            settings.SyncUserId = null;
            settings.SyncUserEmail = null;
            settings.SyncUserDisplayName = null;
            settings.SyncProvider = null;
            settings.LastSyncTimestamp = null;
            settings.AssistantChatsBackfilledAt = null;
            // Clear cross-account sync gate/ETag state so a subsequent login (same or different
            // account) never inherits stale state: a stale LastPushedSettingsHash could suppress
            // the settings row on the next first sync, and stale ETags rely on the server never
            // colliding tag values across accounts.
            settings.LastPushedSettingsHash = null;
            settings.LastChatPullETag = null;
            settings.LastPullETag = null;
            settings.ClientPolicyInitialized = false;
            await _settingsService.SaveSettingsAsync(settings);

            // Sync stays off until the next login, so a kept group policy would go on being applied
            // until then. Cleared after the save, which re-applies policy and would rewrite the cache.
            if (_policyService is not null)
            {
                try
                {
                    await _policyService.ClearServerPolicyAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear the cached group policy");
                }
            }

            LoginStateChanged?.Invoke(this, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout failed");
        }
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false, string? staleAccessToken = null)
    {
        await _loadStoredTokensTask;

        if (!IsLoggedIn || string.IsNullOrEmpty(_encryptedRefreshToken))
            return null;

        if (!forceRefresh && _accessTokenExpiry > DateTime.UtcNow)
        {
            var cached = DecryptAccessToken();
            if (cached is not null)
                return cached;
        }

        // If a caller is retrying a 401, compare against the exact token that failed. If
        // another caller has already refreshed while we waited, return that fresh token.
        var tokenToReplace = staleAccessToken ?? DecryptAccessToken();

        await _refreshLock.WaitAsync();
        try
        {
            var current = DecryptAccessToken();
            if (current is not null)
            {
                if (forceRefresh)
                {
                    if (!string.Equals(current, tokenToReplace, StringComparison.Ordinal))
                        return current;
                }
                else if (_accessTokenExpiry > DateTime.UtcNow)
                {
                    return current;
                }
            }

            var refreshToken = DecryptRefreshToken();
            if (refreshToken is null)
                return null;

            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl))
                return null;

            var reportMetadata = ShouldReportDeviceMetadata(settings);
            using var client = _httpClientFactory.CreateClient();
            if (reportMetadata)
                AttachDeviceMetadata(client, settings);

            var response = await client.PostAsJsonAsync($"{serverUrl}/auth/refresh",
                new { refreshToken });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed with status {Status}", response.StatusCode);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await LogoutAsync();
                }
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var newAccessToken = json.GetProperty("accessToken").GetString();
            var newRefreshToken = json.GetProperty("refreshToken").GetString();
            var expiresIn = json.TryGetProperty("expiresIn", out var lifetime) && lifetime.TryGetInt32(out var seconds)
                ? seconds
                : 0;
            _accessTokenExpiry = AccessTokenExpiryFrom(expiresIn, DateTime.UtcNow);

            _encryptedAccessToken = string.IsNullOrEmpty(newAccessToken)
                ? null : _dpapiHelper.Encrypt(newAccessToken);
            _encryptedRefreshToken = string.IsNullOrEmpty(newRefreshToken)
                ? null : _dpapiHelper.Encrypt(newRefreshToken);

            // Persist new tokens
            settings.EncryptedAccessToken = _encryptedAccessToken;
            settings.EncryptedRefreshToken = _encryptedRefreshToken;
            if (reportMetadata)
                settings.ReportedDeviceMetadata = DeviceMetadataFingerprint();
            await _settingsService.SaveSettingsAsync(settings);

            return newAccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed");
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static int GetRandomPort()
    {
        // Find an available port by binding to port 0
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string BuildLoginSuccessHtml(string? displayName)
    {
        var safe = WebUtility.HtmlEncode(displayName ?? "");
        var greeting = string.IsNullOrEmpty(safe)
            ? _localizationService["Sync_LoginPage_AllSet"]
            : _localizationService.Format("Sync_LoginPage_Welcome", safe);
        var subtitle = _localizationService["Sync_LoginPage_Subtitle"];
        var closeHint = _localizationService["Sync_LoginPage_CloseHint"];
        var fontDataUrl = GetEmbeddedFontDataUrl();

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Pia</title>
        <style>
        @font-face {
            font-family: 'Bricolage Grotesque';
            src: url("{{fontDataUrl}}") format('woff2-variations');
            font-weight: 400 600;
            font-display: block;
        }
        *, *::before, *::after { margin:0; padding:0; box-sizing:border-box; }
        html, body { height:100%; overflow:hidden; }
        body {
            font-family: 'Bricolage Grotesque', Georgia, 'Times New Roman', serif;
            background: #0a0b0f;
            color: #e2e8f0;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        body::before {
            content: '';
            position: fixed;
            inset: 0;
            background: radial-gradient(ellipse 600px 400px at 50% 42%, #0d1a1a 0%, #0a0b0f 100%);
        }
        .wrap {
            position: relative;
            z-index: 1;
            text-align: center;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 1.75rem;
        }
        .icon {
            width: 88px; height: 88px;
            position: relative;
            opacity: 0;
            animation: fadeIn .5s ease-out .15s forwards;
        }
        .glow {
            position: absolute;
            inset: -28px;
            border-radius: 50%;
            background: radial-gradient(circle, rgba(34,211,168,.12) 0%, transparent 70%);
            opacity: 0;
            animation: pulse 3.5s ease-in-out infinite 1.1s;
        }
        .ring {
            width: 88px; height: 88px;
            transform: rotate(-90deg);
        }
        .ring-bg {
            fill: none;
            stroke: rgba(34,211,168,.07);
            stroke-width: 2;
        }
        .ring-fg {
            fill: none;
            stroke: #22d3a8;
            stroke-width: 2.5;
            stroke-linecap: round;
            stroke-dasharray: 264;
            stroke-dashoffset: 264;
            animation: draw .7s cubic-bezier(.4,0,.2,1) .25s forwards;
            filter: drop-shadow(0 0 6px rgba(34,211,168,.25));
        }
        .tick {
            position: absolute;
            top: 50%; left: 50%;
            transform: translate(-50%,-50%);
            width: 36px; height: 36px;
        }
        .tick path {
            fill: none;
            stroke: #22d3a8;
            stroke-width: 3;
            stroke-linecap: round;
            stroke-linejoin: round;
            stroke-dasharray: 34;
            stroke-dashoffset: 34;
            animation: draw .35s ease-out .8s forwards;
        }
        h1 {
            font-size: 1.6rem;
            font-weight: 600;
            letter-spacing: -.015em;
            opacity: 0;
            transform: translateY(10px);
            animation: rise .5s ease-out .65s forwards;
        }
        .sub {
            font-size: .92rem;
            font-weight: 400;
            color: #64748b;
            opacity: 0;
            transform: translateY(10px);
            animation: rise .5s ease-out .85s forwards;
        }
        .hint {
            font-size: .85rem;
            color: #3f4a5c;
            opacity: 0;
            animation: fadeIn .5s ease-out 1.3s forwards;
            margin-top: .5rem;
        }
        .brand {
            position: fixed;
            bottom: 1.5rem;
            font-size: .7rem;
            letter-spacing: .12em;
            text-transform: uppercase;
            color: #1e2330;
            opacity: 0;
            animation: fadeIn .6s ease-out 1.8s forwards;
        }
        @keyframes draw { to { stroke-dashoffset:0 } }
        @keyframes fadeIn { to { opacity:1 } }
        @keyframes rise { to { opacity:1; transform:translateY(0) } }
        @keyframes pulse {
            0%,100% { opacity:.3; transform:scale(1) }
            50% { opacity:.7; transform:scale(1.06) }
        }
        @media (prefers-color-scheme: light) {
            body { background:#f8f7f4; color:#1a1a2e; }
            body::before { background:radial-gradient(ellipse 600px 400px at 50% 42%, #eef6f3 0%, #f8f7f4 100%); }
            .glow { background:radial-gradient(circle, rgba(13,147,115,.1) 0%, transparent 70%); }
            .ring-bg { stroke:rgba(13,147,115,.1); }
            .ring-fg { stroke:#0d9373; filter:drop-shadow(0 0 6px rgba(13,147,115,.2)); }
            .tick path { stroke:#0d9373; }
            .sub { color:#6b7280; }
            .hint { color:#9ca3af; }
            .brand { color:#d4d0c8; }
        }
        </style>
        </head>
        <body>
        <div class="wrap">
            <div class="icon">
                <div class="glow"></div>
                <svg class="ring" viewBox="0 0 88 88">
                    <circle class="ring-bg" cx="44" cy="44" r="42"/>
                    <circle class="ring-fg" cx="44" cy="44" r="42"/>
                </svg>
                <svg class="tick" viewBox="0 0 36 36">
                    <path d="M9 18l7 7 11-13"/>
                </svg>
            </div>
            <div>
                <h1>{{greeting}}</h1>
                <p class="sub">{{subtitle}}</p>
            </div>
            <p class="hint">{{closeHint}}</p>
        </div>
        <div class="brand">Pia</div>
        </body>
        </html>
        """;
    }

    private string BuildLoginErrorHtml(string errorMessage)
    {
        var safe = WebUtility.HtmlEncode(errorMessage);
        var pageTitle = _localizationService["Sync_LoginPage_ErrorTitle"];
        var heading = _localizationService["Sync_LoginPage_ErrorHeading"];
        var closeHint = _localizationService["Sync_LoginPage_CloseHint"];
        var fontDataUrl = GetEmbeddedFontDataUrl();

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>{{pageTitle}}</title>
        <style>
        @font-face {
            font-family: 'Bricolage Grotesque';
            src: url("{{fontDataUrl}}") format('woff2-variations');
            font-weight: 400 600;
            font-display: block;
        }
        *, *::before, *::after { margin:0; padding:0; box-sizing:border-box; }
        html, body { height:100%; overflow:hidden; }
        body {
            font-family: 'Bricolage Grotesque', Georgia, 'Times New Roman', serif;
            background: #0a0b0f;
            color: #e2e8f0;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        body::before {
            content: '';
            position: fixed;
            inset: 0;
            background: radial-gradient(ellipse 600px 400px at 50% 42%, #1a0d0d 0%, #0a0b0f 100%);
        }
        .wrap {
            position: relative;
            z-index: 1;
            text-align: center;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 1.75rem;
        }
        .icon { font-size: 3.5rem; opacity: 0; animation: fadeIn .5s ease-out .15s forwards; }
        h1 { font-size: 1.5rem; font-weight: 600; color: #f87171; opacity: 0; animation: fadeIn .5s ease-out .35s forwards; }
        .sub { font-size: .95rem; color: #94a3b8; margin-top: .5rem; max-width: 400px; opacity: 0; animation: fadeIn .5s ease-out .45s forwards; }
        .hint { font-size: .8rem; color: #475569; opacity: 0; animation: fadeIn .5s ease-out .55s forwards; }
        .brand { position: fixed; bottom: 2rem; font-size: .75rem; letter-spacing: .15em; text-transform: uppercase; color: #1e293b; }
        @keyframes fadeIn { to { opacity: 1; } }
        </style>
        </head>
        <body>
        <div class="wrap">
            <div class="icon">✕</div>
            <div>
                <h1>{{heading}}</h1>
                <p class="sub">{{safe}}</p>
            </div>
            <p class="hint">{{closeHint}}</p>
        </div>
        <div class="brand">Pia</div>
        </body>
        </html>
        """;
    }

    private static string? _cachedFontDataUrl;

    private static string GetEmbeddedFontDataUrl()
    {
        if (_cachedFontDataUrl is not null) return _cachedFontDataUrl;

        var assembly = typeof(AuthService).Assembly;
        const string resourceName = "Pia.Resources.Fonts.BricolageGrotesque-Variable.woff2";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return _cachedFontDataUrl = string.Empty;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());
        return _cachedFontDataUrl = $"data:font/woff2;base64,{base64}";
    }
}
