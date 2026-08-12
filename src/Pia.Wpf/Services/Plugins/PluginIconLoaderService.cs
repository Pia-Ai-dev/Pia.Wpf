using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media.Imaging;
using Pia.Services.Interfaces;

namespace Pia.Services.Plugins;

public class PluginIconLoaderService : IPluginIconLoader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthService _authService;
    private readonly SynchronizationContext _uiContext;

    public PluginIconLoaderService(IHttpClientFactory httpClientFactory, IAuthService authService)
    {
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("PluginIconLoaderService must be created on the UI thread");
    }

    public async Task<object?> LoadIconAsync(string? iconUrl, string? serverUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(iconUrl))
            return null;

        try
        {
            var fullUrl = iconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? iconUrl
                : $"{serverUrl?.TrimEnd('/')}{iconUrl}";

            if (fullUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || fullUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                || fullUrl.EndsWith(".avif", StringComparison.OrdinalIgnoreCase))
                return null;

            byte[]? imageBytes = null;

            if (iconUrl.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                var accessToken = await _authService.GetAccessTokenAsync();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", accessToken);
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var response = await client.GetAsync(fullUrl, ct);
                    if (response.IsSuccessStatusCode)
                        imageBytes = await response.Content.ReadAsByteArrayAsync(ct);
                }
            }
            else
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.GetAsync(fullUrl, ct);
                if (response.IsSuccessStatusCode)
                    imageBytes = await response.Content.ReadAsByteArrayAsync(ct);
            }

            if (imageBytes is null || !IsSupportedImage(imageBytes))
                return null;

            var tcs = new TaskCompletionSource<object?>();
            _uiContext.Post(_ =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 48;
                    bitmap.StreamSource = new MemoryStream(imageBytes);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    tcs.TrySetResult(bitmap);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            return await tcs.Task;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedImage(byte[] data)
    {
        if (data.Length < 8)
            return false;

        return Signatures.Any(s => data.AsSpan(0, s.Length).SequenceEqual(s));
    }

    private static readonly byte[][] Signatures =
    [
        [0x89, 0x50, 0x4E, 0x47],   // PNG
        [0xFF, 0xD8, 0xFF],         // JPEG
        [0x47, 0x49, 0x46, 0x38],   // GIF
        [0x42, 0x4D],               // BMP
        [0x00, 0x00, 0x01, 0x00],   // ICO
        [0x49, 0x49, 0x2A, 0x00],   // TIFF LE
        [0x4D, 0x4D, 0x00, 0x2A],   // TIFF BE
    ];
}
