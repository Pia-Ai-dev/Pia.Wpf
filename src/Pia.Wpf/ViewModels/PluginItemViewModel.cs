using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Shared.Models;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.ViewModels;

public partial class PluginItemViewModel : ObservableObject
{
    private readonly IPluginIconLoader _iconLoader;
    private SyncPlugin? _plugin;

    public Guid Id => _plugin!.Id;
    public string Name => _plugin!.Name;
    public string? Description => _plugin!.Description;
    public string Kind => _plugin!.Kind;
    public string Version => _plugin!.Version;
    public bool IsPreloaded => _plugin!.IsPreloaded;
    public bool IsActive => _plugin!.IsActive;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isActivating;

    [ObservableProperty]
    private object? _iconImage;

    public SymbolRegular FallbackIcon { get; private set; }

    public bool HasIcon => IconImage is not null;

    public string KindBadge => Kind switch
    {
        "builtin_tool_pack" => "Built-in",
        "mcp_server" => "MCP Server",
        "rest_api" => "REST API",
        _ => Kind
    };

    public PluginItemViewModel(IPluginIconLoader iconLoader)
    {
        _iconLoader = iconLoader;
    }

    public void Initialize(SyncPlugin plugin, string? serverUrl)
    {
        _plugin = plugin;
        IsEnabled = plugin.UserEnabled ?? true;
        StatusText = plugin.IsActive ? "Active" : "Inactive";
        FallbackIcon = MapFallbackIcon(plugin);
        _ = LoadIconAsync(plugin.IconUrl, serverUrl);
    }

    public void UpdateStatus(string status)
    {
        StatusText = status;
    }

    private async Task LoadIconAsync(string? iconUrl, string? serverUrl)
    {
        var image = await _iconLoader.LoadIconAsync(iconUrl, serverUrl);
        if (image is not null)
        {
            IconImage = image;
            OnPropertyChanged(nameof(HasIcon));
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
