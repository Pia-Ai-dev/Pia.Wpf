using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// The local MCP surface: list, inline editor, connection probe and the per-server tool allowlist. The
/// allowlist decides what the model is offered at all, which is a different question from the session and
/// "always" grants in Tool permissions.
/// </summary>
public partial class McpServersSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly IPluginService _pluginService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _disposed;
    private bool _suppressReload;

    /// <summary>The allowlist the server was saved with. A stopped server lists no tools to tick, so without
    /// this a save from that state would read the empty list as "no restriction" and open every tool.
    /// Concrete and readonly, refilled in place: the architecture rule bans a mutable interface-typed field.</summary>
    private readonly List<string> _editingAllowedTools = [];

    /// <summary>False means the server is unrestricted, which an empty <see cref="_editingAllowedTools"/>
    /// cannot say on its own — an empty allowlist is a server with every tool withheld.</summary>
    private bool _editingRestrictsTools;

    public ObservableCollection<McpServerRow> Servers { get; } = [];

    /// <summary>Every tool the last probe (or the running server) reported, with its allowlist tick.</summary>
    public ObservableCollection<McpToolRow> Tools { get; } = [];

    [ObservableProperty]
    private bool _hasServers;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private McpServerRow? _selectedServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand))]
    private bool _isEditorOpen;

    [ObservableProperty]
    private Guid? _editingServerId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _editName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private string _editCommand = string.Empty;

    /// <summary>One argument per line — a single box would need shell quoting rules Pia does not apply.</summary>
    [ObservableProperty]
    private string _editArguments = string.Empty;

    /// <summary><c>KEY=value</c> per line.</summary>
    [ObservableProperty]
    private string _editEnvironment = string.Empty;

    [ObservableProperty]
    private string _editWorkingDirectory = string.Empty;

    [ObservableProperty]
    private string _pastedJson = string.Empty;

    [ObservableProperty]
    private string? _editorMessage;

    [ObservableProperty]
    private bool _editorMessageIsError;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _hasTools;

    public bool ShowsDetail => !IsEditorOpen && SelectedServer is not null;
    public bool ShowsPlaceholder => !IsEditorOpen && SelectedServer is null;

    public string? SelectedToolPrefix =>
        SelectedServer is null ? null : _pluginService.GetLocalMcpDefinition(SelectedServer.Id)?.ToolPrefix;

    public McpServersSettingsViewModel(
        IPluginService pluginService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILogger<SettingsViewModel> logger)
    {
        _pluginService = pluginService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _snackbarService = snackbarService;
        _logger = logger;

        _pluginService.PluginsChanged += OnPluginsChanged;
        Reload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pluginService.PluginsChanged -= OnPluginsChanged;
        GC.SuppressFinalize(this);
    }

    private void OnPluginsChanged(object? sender, EventArgs e)
    {
        // A save raises this itself, and reloading mid-save would drop the editor the user is still in.
        if (_suppressReload) return;
        PostOrRun(Reload);
    }

    partial void OnSelectedServerChanged(McpServerRow? oldValue, McpServerRow? newValue)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
        OnPropertyChanged(nameof(SelectedToolPrefix));

        // A reload rebuilds the rows, so the same server returns as a new instance — only a genuinely
        // different one may close an editor mid-edit.
        if (IsEditorOpen && oldValue?.Id != newValue?.Id)
            CloseEditor();
    }

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    private void Reload()
    {
        var selectedId = SelectedServer?.Id;
        Servers.Clear();

        foreach (var plugin in _pluginService.GetLocalMcpPlugins())
        {
            var definition = _pluginService.GetLocalMcpDefinition(plugin.Id);
            var status = _pluginService.GetLocalMcpStatus(plugin.Id);
            var enabled = plugin.UserEnabled ?? true;

            Servers.Add(new McpServerRow(plugin.Id, plugin.Name, DescribeCommand(definition), enabled)
            {
                IsRunning = status.IsRunning,
                StatusText = DescribeStatus(enabled, status)
            });
        }

        HasServers = Servers.Count > 0;
        SelectedServer = Servers.FirstOrDefault(s => s.Id == selectedId) ?? Servers.FirstOrDefault();
    }

    private static string DescribeCommand(LocalMcpDefinition? definition) =>
        definition is null ? "" : string.Join(" ", new[] { definition.Command }.Concat(definition.Args));

    private string DescribeStatus(bool enabled, LocalMcpStatus status)
    {
        if (!enabled) return _localizationService["McpServers_Status_Disabled"];
        if (status.IsRunning) return _localizationService.Format("McpServers_Status_Running", status.ActiveTools.Count);

        return string.IsNullOrWhiteSpace(status.Error)
            ? _localizationService["McpServers_Status_Failed"]
            : _localizationService.Format("McpServers_Status_FailedWithReason", status.Error);
    }

    // ---- editor -------------------------------------------------------------------------------------

    private bool CanAddServer() => !IsEditorOpen;

    [RelayCommand(CanExecute = nameof(CanAddServer))]
    private void AddServer()
    {
        EditingServerId = null;
        EditName = string.Empty;
        EditCommand = string.Empty;
        EditArguments = string.Empty;
        EditEnvironment = string.Empty;
        EditWorkingDirectory = string.Empty;
        PastedJson = string.Empty;
        EditorMessage = null;
        EditorMessageIsError = false;
        SetEditingAllowlist(null);
        Tools.Clear();
        HasTools = false;
        IsEditorOpen = true;
    }

    private bool CanEditSelected() => SelectedServer is not null;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void EditSelected()
    {
        if (SelectedServer is null) return;

        var definition = _pluginService.GetLocalMcpDefinition(SelectedServer.Id);
        if (definition is null) return;

        EditingServerId = SelectedServer.Id;
        EditName = definition.Name;
        EditCommand = definition.Command;
        EditArguments = string.Join(Environment.NewLine, definition.Args);
        EditEnvironment = FormatEnvironment(definition.Env);
        EditWorkingDirectory = definition.WorkingDirectory ?? string.Empty;
        PastedJson = string.Empty;
        EditorMessage = null;
        EditorMessageIsError = false;

        // The running handler is the only place the full tool list survives a restart — the config keeps the
        // allowlist alone, so a stopped server shows nothing to tick until it is tested again.
        SetEditingAllowlist(definition.AllowedTools);
        FillTools(_pluginService.GetLocalMcpStatus(SelectedServer.Id).DiscoveredTools, definition.AllowedTools);

        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEdit() => CloseEditor();

    private void CloseEditor()
    {
        IsEditorOpen = false;
        EditingServerId = null;
        EditorMessage = null;
        EditorMessageIsError = false;
        SetEditingAllowlist(null);
        Tools.Clear();
        HasTools = false;
    }

    [RelayCommand]
    private void ApplyPastedJson()
    {
        var result = LocalMcpJsonParser.Parse(PastedJson, string.IsNullOrWhiteSpace(EditName) ? null : EditName);
        if (!result.Success)
        {
            SetMessage(ErrorText(result), isError: true);
            return;
        }

        var definition = result.Definition!;
        EditName = definition.Name;
        EditCommand = definition.Command;
        EditArguments = string.Join(Environment.NewLine, definition.Args);
        EditEnvironment = FormatEnvironment(definition.Env);
        EditWorkingDirectory = definition.WorkingDirectory ?? string.Empty;
        PastedJson = string.Empty;
        SetMessage(_localizationService["McpServers_JsonApplied"], isError: false);
    }

    private string ErrorText(LocalMcpParseResult result)
    {
        var text = _localizationService[result.Error switch
        {
            LocalMcpParseError.InvalidJson => "McpServers_Error_InvalidJson",
            LocalMcpParseError.NoServerObject => "McpServers_Error_NoServerObject",
            LocalMcpParseError.MultipleServers => "McpServers_Error_MultipleServers",
            LocalMcpParseError.MissingCommand => "McpServers_Error_MissingCommand",
            LocalMcpParseError.RemoteTransport => "McpServers_Error_RemoteTransport",
            LocalMcpParseError.BadArgs => "McpServers_Error_BadArgs",
            _ => "McpServers_Error_BadEnv"
        }];

        return string.IsNullOrWhiteSpace(result.Detail) ? text : $"{text} ({result.Detail})";
    }

    private bool CanTestConnection() => !IsTesting && !string.IsNullOrWhiteSpace(EditCommand);

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestConnectionCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _pluginService.ProbeLocalMcpAsync(BuildDefinition());
            if (!result.Success)
            {
                SetMessage(_localizationService.Format("McpServers_TestFailed", result.Error ?? ""), isError: true);
                return;
            }

            // Ticks already made survive a re-probe; a tool the server has since dropped disappears with it.
            FillTools(result.Tools, CurrentAllowedTools());
            SetMessage(_localizationService.Format("McpServers_TestSucceeded", result.Tools.Count), isError: false);
        }
        finally
        {
            IsTesting = false;
            TestConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetEditingAllowlist(IReadOnlyList<string>? allowed)
    {
        _editingAllowedTools.Clear();
        _editingRestrictsTools = allowed is not null;
        if (allowed is not null)
            _editingAllowedTools.AddRange(allowed);
    }

    private void FillTools(IReadOnlyList<McpProbeTool> discovered, IReadOnlyList<string>? allowed)
    {
        Tools.Clear();
        foreach (var tool in discovered)
        {
            Tools.Add(new McpToolRow(tool.Name, tool.Description, tool.ServerDeclaredDestructive,
                allowed is null || allowed.Contains(tool.Name, StringComparer.Ordinal)));
        }
        HasTools = Tools.Count > 0;
    }

    /// <summary>With nothing to tick, the server's saved allowlist stands — and on a brand-new server that is
    /// null, so an untested one exposes whatever tools it turns out to have rather than none of them.</summary>
    private IReadOnlyList<string>? CurrentAllowedTools() =>
        Tools.Count == 0
            // A copy: the field is refilled in place, so handing out the instance would let closing the
            // editor empty a list the caller is still holding.
            ? (_editingRestrictsTools ? [.. _editingAllowedTools] : null)
            : [.. Tools.Where(t => t.IsAllowed).Select(t => t.Name)];

    [RelayCommand]
    private void SelectAllTools() => SetAllTools(true);

    [RelayCommand]
    private void SelectNoTools() => SetAllTools(false);

    private void SetAllTools(bool allowed)
    {
        foreach (var tool in Tools)
            tool.IsAllowed = allowed;
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(EditName) && !string.IsNullOrWhiteSpace(EditCommand);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var definition = BuildDefinition() with { AllowedTools = CurrentAllowedTools() };

        _suppressReload = true;
        Guid id;
        try
        {
            id = await _pluginService.SaveLocalMcpAsync(EditingServerId, definition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save local MCP server");
            SetMessage(ex.Message, isError: true);
            return;
        }
        finally
        {
            _suppressReload = false;
        }

        CloseEditor();
        Reload();
        SelectedServer = Servers.FirstOrDefault(s => s.Id == id);
        _snackbarService.Show(
            _localizationService["McpServers_Title"],
            _localizationService.Format("McpServers_Saved", definition.Name),
            Wpf.Ui.Controls.ControlAppearance.Success,
            null,
            TimeSpan.FromSeconds(3));
    }

    private LocalMcpDefinition BuildDefinition()
    {
        var name = EditName.Trim();
        return new LocalMcpDefinition(
            name,
            EditCommand.Trim(),
            SplitLines(EditArguments),
            ParseEnvironment(EditEnvironment),
            string.IsNullOrWhiteSpace(EditWorkingDirectory) ? null : EditWorkingDirectory.Trim(),
            LocalMcpConfig.DeriveToolPrefix(name),
            AllowedTools: null);
    }

    private static string FormatEnvironment(IReadOnlyDictionary<string, string> env) =>
        string.Join(Environment.NewLine, env.Select(e => $"{e.Key}={e.Value}"));

    internal static List<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Only the first <c>=</c> splits: a token or a connection string routinely contains more.</summary>
    internal static Dictionary<string, string> ParseEnvironment(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(text))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    private bool CanDeleteSelected() => SelectedServer is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedServer is not { } server) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["McpServers_DeleteTitle"],
            _localizationService.Format("McpServers_DeleteMessage", server.Name));
        if (!confirmed) return;

        await _pluginService.RemoveLocalMcpAsync(server.Id);
        Reload();
    }

    [RelayCommand]
    private async Task ToggleServerAsync(McpServerRow? row)
    {
        if (row is null) return;
        await _pluginService.SetPluginEnabledAsync(row.Id, row.IsEnabled);
    }

    private void SetMessage(string text, bool isError)
    {
        EditorMessage = text;
        EditorMessageIsError = isError;
    }
}
