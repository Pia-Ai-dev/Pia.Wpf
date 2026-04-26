using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

public class CabManagerService
{
    private readonly ILogger<CabManagerService> _logger;
    private readonly TrustedCertificateCacheService _certCache;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;

    private static readonly string PluginsBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "plugins");

    private bool SigningRequired => _configuration.GetValue<bool>("Plugins:SigningRequired", true);

    public CabManagerService(
        ILogger<CabManagerService> logger,
        TrustedCertificateCacheService certCache,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IAuthService authService,
        ISettingsService settingsService)
    {
        _logger = logger;
        _certCache = certCache;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _settingsService = settingsService;
    }

    public async Task<string?> EnsurePluginExtractedAsync(SyncPlugin plugin, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plugin.CabHash))
            return null;

        var pluginDir = Path.Combine(PluginsBasePath, plugin.Id.ToString());
        var hashFile = Path.Combine(pluginDir, ".cab_hash");

        // Check if already extracted with matching hash
        if (Directory.Exists(pluginDir) && File.Exists(hashFile)
            && await File.ReadAllTextAsync(hashFile, ct) == plugin.CabHash)
        {
            return pluginDir;
        }

        try
        {
            // Download cab from GET /api/plugins/{id}/cab
            var cabBytes = await DownloadCabAsync(plugin.Id, ct);

            // Verify SHA-256 hash
            var hash = Convert.ToHexString(SHA256.HashData(cabBytes)).ToLowerInvariant();
            if (hash != plugin.CabHash)
            {
                _logger.LogError("Cab hash mismatch for plugin {Name}: expected {Expected}, got {Actual}",
                    plugin.Name, plugin.CabHash, hash);
                return null;
            }

            // Verify signature if required
            if (SigningRequired && !await VerifySignatureAsync(cabBytes, ct))
            {
                _logger.LogError("Cab signature verification failed for plugin {Name}", plugin.Name);
                return null;
            }

            // Extract cab
            if (Directory.Exists(pluginDir))
                Directory.Delete(pluginDir, true);
            Directory.CreateDirectory(pluginDir);

            var tempCab = Path.Combine(Path.GetTempPath(), $"pia_plugin_{plugin.Id}.cab");
            try
            {
                await File.WriteAllBytesAsync(tempCab, cabBytes, ct);
                await ExtractCabAsync(tempCab, pluginDir, ct);
            }
            finally
            {
                if (File.Exists(tempCab))
                    File.Delete(tempCab);
            }

            // Write hash marker
            await File.WriteAllTextAsync(hashFile, plugin.CabHash, ct);

            _logger.LogInformation("Plugin {Name} extracted to {Path}", plugin.Name, pluginDir);
            return pluginDir;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download/extract cab for plugin {Name}", plugin.Name);
            return null;
        }
    }

    private async Task<byte[]> DownloadCabAsync(Guid pluginId, CancellationToken ct)
    {
        var accessToken = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("No access token available for cab download");

        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrEmpty(settings.ServerUrl))
            throw new InvalidOperationException("No server URL configured for cab download");

        var serverUrl = settings.ServerUrl.TrimEnd('/');
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        client.Timeout = TimeSpan.FromMinutes(5);

        var response = await client.GetAsync($"{serverUrl}/api/plugins/{pluginId}/cab", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<bool> VerifySignatureAsync(byte[] cabBytes, CancellationToken ct)
    {
        // Compare signer thumbprint against trusted certs from _certCache
        var trustedCerts = await _certCache.GetCertificatesAsync(ct);
        if (trustedCerts.Count == 0)
        {
            _logger.LogWarning("No trusted certificates available for verification");
            return false;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, cabBytes, ct);

            // Try to get signer cert from the signed file
            try
            {
#pragma warning disable SYSLIB0057 // X509Certificate.CreateFromSignedFile is obsolete
                var baseCert = X509Certificate.CreateFromSignedFile(tempFile);
#pragma warning restore SYSLIB0057
                using var signerCert = new X509Certificate2(baseCert);
                var thumbprint = signerCert.Thumbprint;
                return trustedCerts.Any(tc =>
                    string.Equals(tc.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            }
            catch (CryptographicException)
            {
                _logger.LogWarning("Cab file is not Authenticode-signed");
                return false;
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static async Task ExtractCabAsync(string cabPath, string targetDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "expand.exe",
            Arguments = $"\"{cabPath}\" -F:* \"{targetDir}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"expand.exe failed (exit {process.ExitCode}): {error}");
        }
    }
}
