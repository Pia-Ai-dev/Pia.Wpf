using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models.Vault;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Drives the Memory screen from the on-disk markdown vault — the source of truth for the assistant's
/// recall — rather than the legacy SQLite JSON store. Items are <see cref="VaultMemoryItem"/> sections
/// addressed by <c>path#heading</c>; edits and deletes go through the vault verbs
/// (<see cref="IMemoryService.UpdateSectionAsync"/> / <see cref="IMemoryService.ForgetAsync"/>) and the
/// vault watcher owns embedding reindex, so this view never generates embeddings.
/// </summary>
public partial class MemoryViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly ILogger<MemoryViewModel> _logger;
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly IClipboardService _clipboardService;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<MemoryGroupViewModel> _memoryGroups = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultOverviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsInspectorPlaceholderVisible))]
    private VaultMemoryItem? _selectedMemory;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultOverviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsInspectorPlaceholderVisible))]
    private int _totalObjectCount;

    // Full-vault composition by canonical category — rebuilt each load from the UNFILTERED snapshot so
    // the chart stays stable during search (MemoryGroups is filtered to recall hits mid-search).
    [ObservableProperty]
    private ObservableCollection<VaultCategorySegment> _vaultComposition = new();

    [ObservableProperty]
    private string _storageSizeText = "0 B";

    [ObservableProperty]
    private bool _isEmbeddingModelAvailable;

    [ObservableProperty]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private float _downloadProgress;

    [ObservableProperty]
    private string _editingData = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private int _embeddingDim = 384;

    [ObservableProperty]
    private bool _isHelpVisible;

    // Right-pane state machine: overview when nothing is selected and the vault has content; the plain
    // "select a memory" placeholder only when the vault is genuinely empty. Both notify when either
    // SelectedMemory or TotalObjectCount changes (see [NotifyPropertyChangedFor] above).
    public bool IsVaultOverviewVisible => SelectedMemory is null && TotalObjectCount > 0;
    public bool IsInspectorPlaceholderVisible => SelectedMemory is null && TotalObjectCount == 0;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> DeleteMemoryCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> EditMemoryCommand { get; }
    public IAsyncRelayCommand SaveEditCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
    public IAsyncRelayCommand DownloadEmbeddingModelCommand { get; }
    public IAsyncRelayCommand RegenerateEmbeddingsCommand { get; }
    public IRelayCommand<VaultMemoryItem> SelectMemoryCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> CopyMarkdownCommand { get; }
    public IRelayCommand ToggleHelpCommand { get; }
    public IRelayCommand OpenVaultFolderCommand { get; }

    public MemoryViewModel(
        ILogger<MemoryViewModel> logger,
        IMemoryService memoryService,
        IEmbeddingService embeddingService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        IClipboardService clipboardService)
    {
        _logger = logger;
        _memoryService = memoryService;
        _embeddingService = embeddingService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _clipboardService = clipboardService;

        RefreshCommand = new AsyncRelayCommand(LoadMemoriesAsync);
        DeleteMemoryCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteDeleteMemory);
        EditMemoryCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteEditMemory);
        SaveEditCommand = new AsyncRelayCommand(ExecuteSaveEdit, CanSaveEdit);
        CancelEditCommand = new RelayCommand(ExecuteCancelEdit);
        DownloadEmbeddingModelCommand = new AsyncRelayCommand(ExecuteDownloadEmbeddingModel);
        RegenerateEmbeddingsCommand = new AsyncRelayCommand(ExecuteRegenerateEmbeddings);
        SelectMemoryCommand = new RelayCommand<VaultMemoryItem>(ExecuteSelectMemory);
        CopyMarkdownCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteCopyMarkdown);
        ToggleHelpCommand = new RelayCommand(() => IsHelpVisible = !IsHelpVisible);
        OpenVaultFolderCommand = new RelayCommand(() => ShellLauncher.RevealInExplorer(_memoryService.VaultRoot));

        PropertyChanged += OnPropertyChanged;
    }

    private async Task ExecuteCopyMarkdown(VaultMemoryItem? memory)
    {
        if (memory is null) return;
        try
        {
            _clipboardService.SetText(memory.Body);
            _snackbarService.Show(_localizationService["Memory_Copied"], string.Empty,
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy memory body to clipboard");
        }
    }

    public void OnNavigatedTo(object? parameter)
    {
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        SelectedMemory = null;
        IsEditing = false;
        IsEmbeddingModelAvailable = _embeddingService.IsModelAvailable;
        await LoadMemoriesAsync();
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadMemoriesAsync()
    {
        try
        {
            IsLoading = true;

            // One enumeration of the vault yields both the items and the storage size.
            var snapshot = await _memoryService.ListMemoriesAsync();
            var items = string.IsNullOrWhiteSpace(SearchQuery)
                ? snapshot.Items
                : ProjectRecallHits(snapshot.Items, await _memoryService.RecallAsync(SearchQuery));

            var groups = BuildGroups(items);

            MemoryGroups.Clear();
            foreach (var group in groups)
            {
                MemoryGroups.Add(group);
            }

            // Keep the inspector on the selected memory only if it is still present (by reference).
            if (SelectedMemory is not null &&
                !MemoryGroups.Any(g => g.Items.Any(m => m.Reference == SelectedMemory.Reference)))
            {
                SelectedMemory = null;
                IsEditing = false;
            }

            // Header count is the total of displayable (canonical-typed) memories — independent of the
            // search filter — so it matches what the unfiltered grouped list shows (no silent divergence).
            TotalObjectCount = CountDisplayable(snapshot.Items);
            StorageSizeText = FormatBytes(snapshot.Bytes);

            // Composition is computed from the UNFILTERED snapshot (not the search-filtered `items`) so the
            // overview bar always agrees with the header total, even mid-search.
            BuildComposition(snapshot.Items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memories");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Project semantic-search hits (RecallAsync indexes ## sections) back to the full VaultMemoryItem
    // (real type/body/updated) by reference. Hits whose section has since changed are dropped; freeform
    // preamble files are not chunked by the indexer, so they never appear here. (Final display order is
    // the canonical group order with alpha-within-group from BuildGroups, not recall rank — D3.)
    private static IReadOnlyList<VaultMemoryItem> ProjectRecallHits(
        IReadOnlyList<VaultMemoryItem> all, IReadOnlyList<RecallHit> hits)
    {
        // Last-wins rather than ToDictionary: a hand-edited file may carry two identical ## headings,
        // which slug-dedup does not collapse, so two items can share a Reference. A throw here would
        // crash search; collapsing is harmless (the path#heading scheme already aliases such sections).
        var byReference = new Dictionary<string, VaultMemoryItem>(StringComparer.Ordinal);
        foreach (var item in all)
        {
            byReference[item.Reference] = item;
        }

        var results = new List<VaultMemoryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var filePath = hit.FilePath.Replace('\\', '/');
            var reference = string.IsNullOrEmpty(hit.Heading) ? filePath : $"{filePath}#{hit.Heading}";
            if (seen.Add(reference) && byReference.TryGetValue(reference, out var item))
            {
                results.Add(item);
            }
        }

        return results;
    }

    // The §8 canonical type set (case-insensitive) — the types the Memory view can group and display.
    private static readonly HashSet<string> DisplayableTypes =
        new(VaultIndexService.CanonicalGroups.Select(g => g.Type), StringComparer.OrdinalIgnoreCase);

    // Count of memories the view can actually show, so the header total matches the grouped list rather
    // than counting hand-edited/foreign-typed records the canonical grouping silently drops.
    private static int CountDisplayable(IReadOnlyList<VaultMemoryItem> items)
        => items.Count(i => DisplayableTypes.Contains(i.Type));

    // Group by the §8 canonical type order with the spec's display names; within a group, items sort
    // alphabetically by title (D3: frontmatter `updated` is document-level, so per-item recency is
    // meaningless). The group timestamp is the newest document `updated` among its items.
    private static List<MemoryGroupViewModel> BuildGroups(IReadOnlyList<VaultMemoryItem> items)
    {
        // Case-insensitive so a case-drifted frontmatter `type` (e.g. "Note") still lands in its group.
        var byType = items
            .GroupBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var groups = new List<MemoryGroupViewModel>();

        foreach (var (type, display) in VaultIndexService.CanonicalGroups)
        {
            if (!byType.TryGetValue(type, out var groupItems))
            {
                continue;
            }

            var ordered = groupItems
                .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            groups.Add(new MemoryGroupViewModel
            {
                Type = type,
                DisplayName = display,
                Items = new ObservableCollection<VaultMemoryItem>(ordered),
                ItemCount = ordered.Count,
                LastUpdated = ordered.Max(i => i.Updated) ?? DateTime.MinValue,
            });
        }

        return groups;
    }

    // Composition-by-category for the Vault Overview: one segment per canonical type present, in the §8
    // CanonicalGroups order, with Fraction = count / totalDisplayable. `total` is the sum over displayable
    // types, i.e. identical to CountDisplayable, so the bar and the header total agree by construction.
    private void BuildComposition(IReadOnlyList<VaultMemoryItem> items)
    {
        var byType = items
            .Where(i => DisplayableTypes.Contains(i.Type))
            .GroupBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var total = byType.Values.Sum();

        VaultComposition.Clear();
        if (total == 0)
        {
            return; // Divide-by-zero guard: an empty (or all-foreign-typed) vault emits no segments.
        }

        foreach (var (type, display) in VaultIndexService.CanonicalGroups)
        {
            if (!byType.TryGetValue(type, out var count) || count == 0)
            {
                continue;
            }

            VaultComposition.Add(new VaultCategorySegment(type, display, count, count / (double)total));
        }
    }

    private async Task ExecuteDeleteMemory(VaultMemoryItem? memory)
    {
        if (memory is null) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Memory_DeleteTitle"],
            _localizationService.Format("Msg_Memory_DeleteConfirm", memory.Title));

        if (!confirmed) return;

        try
        {
            await _memoryService.ForgetAsync(memory.Reference);

            // Remove from the group.
            foreach (var group in MemoryGroups)
            {
                if (group.Items.Remove(memory))
                {
                    group.ItemCount = group.Items.Count;
                    if (group.Items.Count == 0)
                    {
                        MemoryGroups.Remove(group);
                    }
                    break;
                }
            }

            if (SelectedMemory == memory)
            {
                SelectedMemory = null;
                IsEditing = false;
            }

            var snapshot = await _memoryService.ListMemoriesAsync();
            TotalObjectCount = CountDisplayable(snapshot.Items);
            StorageSizeText = FormatBytes(snapshot.Bytes);
            BuildComposition(snapshot.Items);

            _snackbarService.Show(_localizationService["Msg_Memory_Deleted"], _localizationService.Format("Msg_Memory_MemoryDeleted", memory.Title),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete memory");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_DeleteFailed", ex.Message));
        }
    }

    private Task ExecuteEditMemory(VaultMemoryItem? memory)
    {
        if (memory is null) return Task.CompletedTask;

        SelectedMemory = memory;
        EditingData = memory.Body;
        IsEditing = true;
        SaveEditCommand.NotifyCanExecuteChanged();

        return Task.CompletedTask;
    }

    private bool CanSaveEdit() => IsEditing && SelectedMemory is not null;

    private async Task ExecuteSaveEdit()
    {
        if (SelectedMemory is null) return;

        var reference = SelectedMemory.Reference;
        try
        {
            // Whole-body replace through the vault; the watcher reindexes embeddings on the file change.
            await _memoryService.UpdateSectionAsync(reference, EditingData);

            IsEditing = false;
            await LoadMemoriesAsync();

            // Re-select the saved memory by reference so the inspector keeps focus on it.
            foreach (var group in MemoryGroups)
            {
                var match = group.Items.FirstOrDefault(m => m.Reference == reference);
                if (match is not null)
                {
                    SelectedMemory = match;
                    break;
                }
            }

            _snackbarService.Show(_localizationService["Msg_Memory_Saved"], _localizationService["Msg_Memory_MemoryUpdated"],
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save memory edit");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_SaveFailed", ex.Message));
        }
    }

    private void ExecuteCancelEdit()
    {
        IsEditing = false;
        EditingData = string.Empty;
    }

    private void ExecuteSelectMemory(VaultMemoryItem? memory)
    {
        if (memory is not null)
        {
            SelectedMemory = memory;
        }
    }

    private async Task ExecuteDownloadEmbeddingModel()
    {
        if (IsDownloadingModel) return;

        IsDownloadingModel = true;
        DownloadProgress = 0;

        try
        {
            var progress = new Progress<float>(p => DownloadProgress = p);
            var success = await _embeddingService.DownloadModelAsync(progress);

            IsEmbeddingModelAvailable = _embeddingService.IsModelAvailable;

            if (success)
            {
                _snackbarService.Show(_localizationService["Msg_Memory_Downloaded"], _localizationService["Msg_Memory_EmbeddingModelDownloaded"],
                    Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
            else
            {
                await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Memory_DownloadFailedTitle"],
                    _localizationService["Msg_Memory_DownloadFailedMessage"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download embedding model");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_DownloadError", ex.Message));
        }
        finally
        {
            IsDownloadingModel = false;
        }
    }

    // NOTE: the embedding model UI (download / regenerate) is deferred for removal — the vault watcher
    // owns reindex, so the save path no longer regenerates embeddings. This command still operates on the
    // legacy table and is kept only until the status-bar embedding affordance is retired (follow-up).
    private async Task ExecuteRegenerateEmbeddings()
    {
        if (!_embeddingService.IsModelAvailable)
        {
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Memory_ModelNotAvailableTitle"],
                _localizationService["Msg_Memory_ModelNotAvailableMessage"]);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Memory_RegenerateEmbeddingsTitle"],
            _localizationService["Msg_Memory_RegenerateEmbeddingsMessage"]);

        if (!confirmed) return;

        try
        {
            IsLoading = true;

            var allMemories = await _memoryService.GetAllObjectsAsync();
            var total = allMemories.Count;
            var processed = 0;

            foreach (var memory in allMemories)
            {
                try
                {
                    var textToEmbed = $"{memory.Label} {memory.Data}";
                    var embedding = await _embeddingService.GenerateEmbeddingAsync(textToEmbed);
                    await _memoryService.UpdateEmbeddingAsync(memory.Id, _embeddingService.FloatsToBytes(embedding));
                    processed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate embedding for {Id}", memory.Id);
                }
            }

            _snackbarService.Show(_localizationService["Msg_Memory_Complete"], _localizationService.Format("Msg_Memory_EmbeddingsRegenerated", processed, total),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate embeddings");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_RegenerateFailed", ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void DebounceSearch()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        DebounceAsync(500, LoadMemoriesAsync, token).SafeFireAndForget(_logger);
    }

    private static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchQuery))
        {
            DebounceSearch();
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        PropertyChanged -= OnPropertyChanged;

        GC.SuppressFinalize(this);
    }
}

public partial class MemoryGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<VaultMemoryItem> _items = new();

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private DateTime _lastUpdated;

    [ObservableProperty]
    private bool _isExpanded = true;
}
