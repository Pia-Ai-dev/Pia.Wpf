using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Shared.Auth;

namespace Pia.Services;

public class AuthService : IAuthService
{
    private readonly ISettingsService _settingsService;
    private readonly DpapiHelper _dpapiHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthService> _logger;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _accessTokenExpiry;

    public bool IsLoggedIn { get; private set; }
    public string? UserDisplayName { get; private set; }
    public string? UserEmail { get; private set; }
    public string? Provider { get; private set; }

    public event EventHandler<bool>? LoginStateChanged;

    public AuthService(
        ISettingsService settingsService,
        DpapiHelper dpapiHelper,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthService> logger)
    {
        _settingsService = settingsService;
        _dpapiHelper = dpapiHelper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _ = LoadStoredTokensAsync();
    }

    private async Task LoadStoredTokensAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (!settings.SyncEnabled || string.IsNullOrEmpty(settings.EncryptedRefreshToken))
                return;

            _refreshToken = _dpapiHelper.Decrypt(settings.EncryptedRefreshToken);
            UserDisplayName = settings.SyncUserDisplayName;
            UserEmail = settings.SyncUserEmail;
            Provider = settings.SyncProvider;

            if (!string.IsNullOrEmpty(_refreshToken))
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

            // Find an available loopback port for the redirect
            var port = GetRandomPort();
            var redirectUri = $"http://localhost:{port}/";

            // Open system browser to server's OAuth login endpoint
            var loginUrl = $"{serverUrl}/auth/login?provider={provider}&redirect_uri={Uri.EscapeDataString(redirectUri)}";
            Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });

            // Start a loopback HTTP listener to receive the callback
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            // Wait for the callback (timeout after 5 minutes)
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var context = await listener.GetContextAsync().WaitAsync(cts.Token);

            // Parse the tokens from the callback URL query string
            var query = context.Request.QueryString;
            var error = query["error"];
            var errorMessage = query["message"];
            var accessToken = query["access_token"];
            var refreshToken = query["refresh_token"];
            var email = query["email"];
            var displayName = query["display_name"];
            var userId = query["user_id"];

            // Send appropriate response to the browser
            var browserHtml = string.IsNullOrEmpty(error)
                ? BuildLoginSuccessHtml(displayName)
                : BuildLoginErrorHtml(errorMessage ?? "Login failed");
            var responseBytes = System.Text.Encoding.UTF8.GetBytes(browserHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
            listener.Stop();

            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("OAuth callback returned error: {Error} - {Message}", error, errorMessage);
                return (false, errorMessage ?? "Login failed");
            }

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogWarning("OAuth callback missing tokens");
                return (false, "Login failed - no tokens received");
            }

            // Store tokens
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _accessTokenExpiry = DateTime.UtcNow.AddMinutes(14); // Slightly less than 15 min
            UserDisplayName = displayName;
            UserEmail = email;
            Provider = provider;
            IsLoggedIn = true;

            // Persist to settings
            settings.SyncEnabled = true;
            settings.EncryptedAccessToken = _dpapiHelper.Encrypt(accessToken);
            settings.EncryptedRefreshToken = _dpapiHelper.Encrypt(refreshToken);
            settings.SyncUserId = userId;
            settings.SyncUserEmail = email;
            settings.SyncUserDisplayName = displayName;
            settings.SyncProvider = provider;
            settings.SyncDeviceId ??= Guid.NewGuid().ToString();
            await _settingsService.SaveSettingsAsync(settings);

            LoginStateChanged?.Invoke(this, true);
            return (true, null);
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

            using var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{serverUrl}/auth/login/local",
                new LocalLoginRequest { Email = email, Password = password });

            if (!response.IsSuccessStatusCode)
            {
                var errorJson = await response.Content.ReadFromJsonAsync<JsonElement>();
                var errorMessage = errorJson.TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : "Login failed";
                return (false, errorMessage);
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<LocalLoginResponse>();
            if (loginResponse is null)
                return (false, "Invalid server response");

            _accessToken = loginResponse.AccessToken;
            _refreshToken = loginResponse.RefreshToken;
            _accessTokenExpiry = DateTime.UtcNow.AddMinutes(14);
            UserDisplayName = loginResponse.User.DisplayName;
            UserEmail = loginResponse.User.Email;
            Provider = "local";
            IsLoggedIn = true;

            settings.SyncEnabled = true;
            settings.EncryptedAccessToken = _dpapiHelper.Encrypt(loginResponse.AccessToken);
            settings.EncryptedRefreshToken = _dpapiHelper.Encrypt(loginResponse.RefreshToken);
            settings.SyncUserId = loginResponse.User.Id.ToString();
            settings.SyncUserEmail = loginResponse.User.Email;
            settings.SyncUserDisplayName = loginResponse.User.DisplayName;
            settings.SyncProvider = "local";
            settings.SyncDeviceId ??= Guid.NewGuid().ToString();
            await _settingsService.SaveSettingsAsync(settings);

            LoginStateChanged?.Invoke(this, true);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local login failed");
            return (false, "Connection error. Please check the server URL.");
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');

            // Revoke refresh token on server
            if (!string.IsNullOrEmpty(serverUrl) && !string.IsNullOrEmpty(_refreshToken))
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    await client.PostAsJsonAsync($"{serverUrl}/auth/logout",
                        new { refreshToken = _refreshToken });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to revoke refresh token on server");
                }
            }

            // Clear local state
            _accessToken = null;
            _refreshToken = null;
            UserDisplayName = null;
            UserEmail = null;
            Provider = null;
            IsLoggedIn = false;

            settings.SyncEnabled = false;
            settings.EncryptedAccessToken = null;
            settings.EncryptedRefreshToken = null;
            settings.SyncUserId = null;
            settings.SyncUserEmail = null;
            settings.SyncUserDisplayName = null;
            settings.SyncProvider = null;
            settings.LastSyncTimestamp = null;
            await _settingsService.SaveSettingsAsync(settings);

            LoginStateChanged?.Invoke(this, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout failed");
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        if (!IsLoggedIn || string.IsNullOrEmpty(_refreshToken))
            return null;

        if (_accessToken is not null && _accessTokenExpiry > DateTime.UtcNow)
            return _accessToken;

        // Refresh the access token
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var serverUrl = settings.ServerUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(serverUrl))
                return null;

            using var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{serverUrl}/auth/refresh",
                new { refreshToken = _refreshToken });

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
            _accessToken = json.GetProperty("accessToken").GetString();
            _refreshToken = json.GetProperty("refreshToken").GetString();
            _accessTokenExpiry = DateTime.UtcNow.AddMinutes(14);

            // Persist new tokens
            settings.EncryptedAccessToken = _dpapiHelper.Encrypt(_accessToken!);
            settings.EncryptedRefreshToken = _dpapiHelper.Encrypt(_refreshToken!);
            await _settingsService.SaveSettingsAsync(settings);

            return _accessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed");
            return null;
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

    private static string BuildLoginSuccessHtml(string? displayName)
    {
        var safe = WebUtility.HtmlEncode(displayName ?? "");
        var greeting = string.IsNullOrEmpty(safe) ? "You're all set" : $"Welcome, {safe}";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Pia</title>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,400;12..96,600&display=swap" rel="stylesheet">
        <style>
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
                <p class="sub">Signed in to Pia</p>
            </div>
            <p class="hint">You can close this tab</p>
        </div>
        <div class="brand">Pia</div>
        </body>
        </html>
        """;
    }

    private static string BuildLoginErrorHtml(string errorMessage)
    {
        var safe = WebUtility.HtmlEncode(errorMessage);

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Pia – Login Error</title>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,400;12..96,600&display=swap" rel="stylesheet">
        <style>
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
                <h1>Sign in failed</h1>
                <p class="sub">{{safe}}</p>
            </div>
            <p class="hint">You can close this tab</p>
        </div>
        <div class="brand">Pia</div>
        </body>
        </html>
        """;
    }
}
