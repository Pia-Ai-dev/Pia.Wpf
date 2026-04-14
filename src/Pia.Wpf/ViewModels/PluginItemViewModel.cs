using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Shared.Models;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.ViewModels;

public partial class PluginItemViewModel : ObservableObject
{
    private readonly SyncPlugin _plugin;

    public Guid Id => _plugin.Id;
    public string Name => _plugin.Name;
    public string? Description => _plugin.Description;
    public string Kind => _plugin.Kind;
    public string Version => _plugin.Version;
    public bool IsPreloaded => _plugin.IsPreloaded;
    public bool IsActive => _plugin.IsActive;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isActivating;

    [ObservableProperty]
    private ImageSource? _iconImage;

    /// <summary>Fluent symbol icon used as fallback when no image is available.</summary>
    public SymbolRegular FallbackIcon { get; }

    /// <summary>True when IconImage loaded successfully.</summary>
    public bool HasIcon => IconImage is not null;

    public string KindBadge => Kind switch
    {
        "builtin_tool_pack" => "Built-in",
        "mcp_server" => "MCP Server",
        "rest_api" => "REST API",
        _ => Kind
    };

    public PluginItemViewModel(SyncPlugin plugin, string? serverUrl,
        IHttpClientFactory? httpClientFactory = null, IAuthService? authService = null)
    {
        _plugin = plugin;
        _isEnabled = plugin.UserEnabled ?? true;
        _statusText = plugin.IsActive ? "Active" : "Inactive";
        FallbackIcon = MapFallbackIcon(plugin);
        _ = LoadIconAsync(plugin.IconUrl, serverUrl, httpClientFactory, authService);
    }

    public void UpdateStatus(string status)
    {
        StatusText = status;
    }

    private async Task LoadIconAsync(string? iconUrl, string? serverUrl,
        IHttpClientFactory? httpClientFactory, IAuthService? authService)
    {
        if (string.IsNullOrEmpty(iconUrl))
            return;

        try
        {
            var fullUrl = iconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? iconUrl
                : $"{serverUrl?.TrimEnd('/')}{iconUrl}";

            // Skip formats WPF can't decode natively
            if (fullUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || fullUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                || fullUrl.EndsWith(".avif", StringComparison.OrdinalIgnoreCase))
                return;

            byte[]? imageBytes = null;

            if (httpClientFactory is not null && authService is not null
                && iconUrl.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                // Authenticated download for server API endpoints
                var accessToken = await authService.GetAccessTokenAsync();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    using var client = httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", accessToken);
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var response = await client.GetAsync(fullUrl);
                    if (response.IsSuccessStatusCode)
                        imageBytes = await response.Content.ReadAsByteArrayAsync();
                }
            }
            else
            {
                // Download external URLs ourselves so we can validate before decoding
                using var client = httpClientFactory?.CreateClient() ?? new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.GetAsync(fullUrl);
                if (response.IsSuccessStatusCode)
                    imageBytes = await response.Content.ReadAsByteArrayAsync();
            }

            if (imageBytes is null || !IsSupportedImage(imageBytes))
                return;

            App.Current.Dispatcher.Invoke(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 48;
                bitmap.StreamSource = new MemoryStream(imageBytes);
                bitmap.EndInit();
                bitmap.Freeze();
                IconImage = bitmap;
                OnPropertyChanged(nameof(HasIcon));
            });
        }
        catch
        {
            // Fall back to symbol icon
        }
    }

    private static bool IsSupportedImage(byte[] data)
    {
        if (data.Length < 8)
            return false;

        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return true;
        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return true;
        // GIF: GIF8
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return true;
        // BMP: BM
        if (data[0] == 0x42 && data[1] == 0x4D)
            return true;
        // ICO: 00 00 01 00
        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
            return true;
        // TIFF: 49 49 2A 00 or 4D 4D 00 2A
        if ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00)
            || (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A))
            return true;

        return false;
    }

    private static SymbolRegular MapFallbackIcon(SyncPlugin plugin)
    {
        if (plugin.Kind == "builtin_tool_pack")
        {
            return plugin.Name.ToLowerInvariant() switch
            {
                "memory" => SymbolRegular.BrainCircuit24,
                "todo" => SymbolRegular.TaskListSquareLtr24,
                "reminder" => SymbolRegular.Alert24,
                _ => SymbolRegular.PuzzlePiece24
            };
        }

        return plugin.Kind switch
        {
            "mcp_server" => SymbolRegular.PlugConnected24,
            "rest_api" => SymbolRegular.Globe24,
            _ => SymbolRegular.PuzzlePiece24
        };
    }
}
