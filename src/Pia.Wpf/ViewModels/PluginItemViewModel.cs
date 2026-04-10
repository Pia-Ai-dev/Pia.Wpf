using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Shared.Models;
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

    public PluginItemViewModel(SyncPlugin plugin, string? serverUrl)
    {
        _plugin = plugin;
        _isEnabled = plugin.UserEnabled ?? true;
        _statusText = plugin.IsActive ? "Active" : "Inactive";
        FallbackIcon = MapFallbackIcon(plugin);
        LoadIcon(plugin.IconUrl, serverUrl);
    }

    public void UpdateStatus(string status)
    {
        StatusText = status;
    }

    private void LoadIcon(string? iconUrl, string? serverUrl)
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

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(fullUrl, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48;
            bitmap.EndInit();
            IconImage = bitmap;
            OnPropertyChanged(nameof(HasIcon));
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
