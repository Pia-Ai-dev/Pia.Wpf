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

            // Skip SVG — WPF can't render it natively
            if (fullUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return;

            byte[]? imageBytes = null;

            // Use authenticated download for server API endpoints
            if (httpClientFactory is not null && authService is not null
                && iconUrl.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
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

            App.Current.Dispatcher.Invoke(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 48;

                if (imageBytes is not null)
                {
                    bitmap.StreamSource = new MemoryStream(imageBytes);
                }
                else
                {
                    bitmap.UriSource = new Uri(fullUrl, UriKind.Absolute);
                }

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
