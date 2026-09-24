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
    private bool _reloading;

    /// <summary>Starting or stopping a server owns a subprocess, and PluginService mutates one shared catalogue
    /// while it does, so the calls are serialised here rather than by disabling every row's switch.</summary>
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    /// <summary>Toggles in flight, by server id and the state each is moving to. <see cref="Reload"/> projects
    /// it, so a rebuild cannot drop the spinner off a row whose subprocess is still coming up.</summary>
    private readonly Dictionary<Guid, bool> _pendingToggles = [];

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

    /// <summary>What the detail pane lists for the selected server. Separate from <see cref="Tools"/>, which
    /// carries the editor's two-way ticks and is emptied when the editor closes.</summary>
    public ObservableCollection<McpServerToolInfo> SelectedTools { get; } = [];

    [ObservableProperty]
    private bool _hasServers;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestSelectedCommand))]
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

    /// <summary>A save stops and restarts the subprocess, which takes seconds the view has to account for.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelEditCommand))]
    private bool _isSaving;

    [ObservableProperty]
    private bool _hasTools;

    [ObservableProperty]
    private string? _selectedToolPrefix;

    [ObservableProperty]
    private string? _selectedWorkingDirectory;

    /// <summary>Key names only: the values are the tokens these servers authenticate with.</summary>
    [ObservableProperty]
    private string? _selectedEnvironmentKeys;

    [ObservableProperty]
    private string? _selectedError;

    [ObservableProperty]
    private string? _selectedToolsSummary;

    [ObservableProperty]
    private string? _selectedToolsHint;

    [ObservableProperty]
    private bool _hasSelectedTools;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSelectedCommand))]
    private bool _isTestingSelected;

    [ObservableProperty]
    private string? _detailMessage;

    [ObservableProperty]
    private bool _detailMessageIsError;

    public bool ShowsDetail => !IsEditorOpen && SelectedServer is not null;
    public bool ShowsPlaceholder => !IsEditorOpen && SelectedServer is null;

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
        RefreshDetail();

        // Only a selection the USER changed may close an open editor. Comparing ids is not enough: a reload
        // clears the list, and the ListBox writes null back through the two-way binding on the way, which
        // reads as a different server and would discard whatever is half-typed.
        if (!_reloading && IsEditorOpen && oldValue?.Id != newValue?.Id)
            CloseEditor();
    }

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    private void Reload()
    {
        _reloading = true;
        try
        {
            ReloadCore();
        }
        finally
        {
            _reloading = false;
        }
    }

    private void ReloadCore()
    {
        var selectedId = SelectedServer?.Id;
        Servers.Clear();

        foreach (var plugin in _pluginService.GetLocalMcpPlugins())
        {
            var definition = _pluginService.GetLocalMcpDefinition(plugin.Id);
            var status = _pluginService.GetLocalMcpStatus(plugin.Id);
            var enabled = plugin.UserEnabled ?? true;
            var pending = _pendingToggles.TryGetValue(plugin.Id, out var target);

            Servers.Add(new McpServerRow(plugin.Id, plugin.Name, DescribeCommand(definition), pending ? target : enabled)
            {
                IsRunning = !pending && enabled && status.IsRunning,
                IsFailed = !pending && enabled && !status.IsRunning,
                IsToggling = pending,
                StatusText = pending ? PendingStatus(target) : DescribeStatus(enabled, status)
            });
        }

        HasServers = Servers.Count > 0;

        var reselected = Servers.FirstOrDefault(s => s.Id == selectedId) ?? Servers.FirstOrDefault();
        if (ReferenceEquals(reselected, SelectedServer))
            RefreshDetail();
        else
            SelectedServer = reselected;
    }

    private string PendingStatus(bool target) =>
        _localizationService[target ? "McpServers_Status_Starting" : "McpServers_Status_Stopping"];

    /// <summary>Everything the detail pane reads, from one definition/status pair: reading the definition
    /// decrypts its env values, so it is not something a binding should trigger per property.</summary>
    private void RefreshDetail()
    {
        var definition = SelectedServer is null ? null : _pluginService.GetLocalMcpDefinition(SelectedServer.Id);
        var status = SelectedServer is null ? null : _pluginService.GetLocalMcpStatus(SelectedServer.Id);

        DetailMessage = null;
        DetailMessageIsError = false;
        SelectedToolPrefix = NullIfBlank(definition?.ToolPrefix);
        SelectedWorkingDirectory = NullIfBlank(definition?.WorkingDirectory);
        SelectedEnvironmentKeys = definition is { Env.Count: > 0 } ? string.Join(", ", definition.Env.Keys) : null;
        SelectedError = SelectedServer is { IsFailed: true } ? NullIfBlank(status?.Error) : null;

        FillSelectedTools(definition?.AllowedTools, status?.DiscoveredTools ?? []);
    }

    private void FillSelectedTools(IReadOnlyList<string>? allowed, IReadOnlyList<McpProbeTool> discovered)
    {
        SelectedTools.Clear();

        if (discovered.Count > 0)
        {
            foreach (var tool in discovered)
            {
                SelectedTools.Add(new McpServerToolInfo(tool.Name, tool.Description, tool.ServerDeclaredDestructive,
                    allowed is null || allowed.Contains(tool.Name, StringComparer.Ordinal)));
            }

            SelectedToolsSummary = _localizationService.Format("McpServers_Detail_ToolsSummary",
                SelectedTools.Count(t => t.IsAllowed), SelectedTools.Count);
            SelectedToolsHint = null;
        }
        else if (allowed is { Count: > 0 })
        {
            // A stopped server reports nothing, so its saved allowlist is the only tool list left to show.
            foreach (var name in allowed)
                SelectedTools.Add(new McpServerToolInfo(name, null, false, true));

            SelectedToolsSummary = _localizationService.Format("McpServers_Detail_ToolsAllowed", allowed.Count);
            SelectedToolsHint = _localizationService["McpServers_Detail_ToolsStopped"];
        }
        else
        {
            SelectedToolsSummary = null;
            SelectedToolsHint = _localizationService["McpServers_Detail_ToolsUnknown"];
        }

        HasSelectedTools = SelectedTools.Count > 0;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

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

    private bool CanTestSelected() => SelectedServer is not null && !IsTestingSelected;

    /// <summary>Probes the SAVED definition, so a stopped server can list its tools without opening the
    /// editor — the state the detail pane otherwise has nothing to show for.</summary>
    [RelayCommand(CanExecute = nameof(CanTestSelected))]
    private async Task TestSelectedAsync()
    {
        if (SelectedServer is null) return;

        var definition = _pluginService.GetLocalMcpDefinition(SelectedServer.Id);
        if (definition is null) return;

        IsTestingSelected = true;
        DetailMessage = null;
        try
        {
            var result = await _pluginService.ProbeLocalMcpAsync(definition);
            if (!result.Success)
            {
                SetDetailMessage(_localizationService.Format("McpServers_TestFailed", result.Error ?? ""), isError: true);
                return;
            }

            FillSelectedTools(definition.AllowedTools, result.Tools);
            SetDetailMessage(_localizationService.Format("McpServers_TestSucceeded", result.Tools.Count), isError: false);
        }
        finally
        {
            IsTestingSelected = false;
        }
    }

    private bool CanCancelEdit() => !IsSaving;

    // Leaving the editor mid-save lets the save's own CloseEditor land on whatever was opened after it.
    [RelayCommand(CanExecute = nameof(CanCancelEdit))]
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
        IsSaving = true;
        EditorMessage = null;
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
            IsSaving = false;
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

    // Concurrent by design: one command instance serves every row, so the default serialisation reports
    // CanExecute false and greys out every other switch for as long as one subprocess takes to come up.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleServerAsync(McpServerRow? row)
    {
        if (row is null || _pendingToggles.ContainsKey(row.Id)) return;

        var target = row.IsEnabled;
        _pendingToggles[row.Id] = target;
        Reload();

        await _toggleGate.WaitAsync();
        try
        {
            await _pluginService.SetPluginEnabledAsync(row.Id, target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle local MCP server {PluginId}", row.Id);
            _snackbarService.Show(
                _localizationService["McpServers_Title"],
                ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            _pendingToggles.Remove(row.Id);
            _toggleGate.Release();
            Reload();
        }
    }

    private void SetDetailMessage(string text, bool isError)
    {
        DetailMessage = text;
        DetailMessageIsError = isError;
    }

    private void SetMessage(string text, bool isError)
    {
        EditorMessage = text;
        EditorMessageIsError = isError;
    }
}
