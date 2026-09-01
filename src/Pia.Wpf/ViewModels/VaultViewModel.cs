using System.Collections.ObjectModel;
using System.IO;
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
/// Drives the Vault view from the on-disk markdown vault — the source of truth for the assistant's
/// recall — rather than the legacy SQLite JSON store. Items are <see cref="VaultMemoryItem"/> sections
/// addressed by <c>path#heading</c>; edits and deletes go through the vault verbs
/// (<see cref="IMemoryService.UpdateSectionAsync"/> / <see cref="IMemoryService.ForgetAsync"/>) and the
/// vault watcher owns embedding reindex, so this view never generates embeddings.
/// </summary>
public partial class VaultViewModel : UiThreadViewModel, INavigationAware, IDisposable
{
    private readonly ILogger<VaultViewModel> _logger;
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly IClipboardService _clipboardService;
    private readonly IVaultSourcesService _vaultSourcesService;
    private readonly IIngestScheduler _ingestScheduler;
    private readonly ISettingsService _settingsService;
    private readonly IObsidianService _obsidianService;
    private readonly ILintService _lintService;
    private readonly ICharterDrafter _charterDrafter;
    private readonly IVaultCharterService _charterService;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _rebuildCts;
    private bool _disposed;

    // Browser-style back history of visited page references (most-recent last), capped at MaxHistory.
    // Recorded on every page change in OnSelectedMemoryChanged; GoBack pops it. _suppressHistory guards the
    // programmatic re-selection GoBack itself performs so it does not re-record.
    private const int MaxHistory = 10;
    private readonly List<string> _backStack = new();
    private bool _suppressHistory;

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

    // The sources/ RAW layer for the overview's "Source documents" section — display-ready rows (the
    // status line is localized here, not in XAML) plus the count that gates the overview visibility.
    [ObservableProperty]
    private ObservableCollection<VaultSourceRow> _sourceFiles = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultOverviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsInspectorPlaceholderVisible))]
    private int _sourceFileCount;

    [ObservableProperty]
    private string _sourcesSummaryText = string.Empty;

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
    private bool _isRebuilding;

    [ObservableProperty]
    private string _rebuildStatusText = string.Empty;

    [ObservableProperty]
    private int _embeddingDim = 384;

    // The charter decides which topics earn a page and is the one ingest lever with no UI. Empty
    // until the user writes or accepts one — a placeholder would become the model's grounding.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCharter))]
    private string _charterText = string.Empty;

    [ObservableProperty]
    private string _charterDraft = string.Empty;

    [ObservableProperty]
    private bool _isEditingCharter;

    [ObservableProperty]
    private bool _isDraftingCharter;

    public bool HasCharter => !string.IsNullOrWhiteSpace(CharterText);

    // Right-pane state machine: overview when nothing is selected and the vault has content — memories
    // OR staged source documents (a sources-only vault still has something worth showing); the plain
    // "select a memory" placeholder only when the vault is genuinely empty. All three inputs notify
    // (see [NotifyPropertyChangedFor] above).
    public bool IsVaultOverviewVisible
        => SelectedMemory is null && (TotalObjectCount > 0 || SourceFileCount > 0);
    public bool IsInspectorPlaceholderVisible
        => SelectedMemory is null && TotalObjectCount == 0 && SourceFileCount == 0;

    /// <summary>Gates both Obsidian buttons; the launcher caches its probe, so this is not re-detected per read.</summary>
    public bool IsObsidianAvailable => _obsidianService.IsAvailable;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> DeleteMemoryCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> EditMemoryCommand { get; }
    public IAsyncRelayCommand SaveEditCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
    public IAsyncRelayCommand DownloadEmbeddingModelCommand { get; }
    public IAsyncRelayCommand RegenerateEmbeddingsCommand { get; }
    public IRelayCommand<VaultMemoryItem> SelectMemoryCommand { get; }
    public IAsyncRelayCommand<string> NavigateToLinkCommand { get; }
    public IAsyncRelayCommand GoBackCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> CopyMarkdownCommand { get; }
    public IAsyncRelayCommand ShowHelpCommand { get; }
    public IRelayCommand GoHomeCommand { get; }
    public IRelayCommand OpenVaultFolderCommand { get; }
    public IAsyncRelayCommand OpenVaultInObsidianCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> OpenNoteInObsidianCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<string>> AddSourceFilesCommand { get; }
    public IAsyncRelayCommand<VaultMemoryItem> RebuildPageCommand { get; }
    public IAsyncRelayCommand RebuildAllPagesCommand { get; }

    /// <summary>Dry-run the coherence pass, show what it would change, apply only on confirm.</summary>
    public IAsyncRelayCommand CleanUpVaultCommand { get; }

    public IAsyncRelayCommand DraftCharterCommand { get; }
    public IAsyncRelayCommand SaveCharterCommand { get; }
    public IRelayCommand EditCharterCommand { get; }
    public IRelayCommand CancelCharterEditCommand { get; }
    public IRelayCommand CancelRebuildCommand { get; }

    public VaultViewModel(
        ILogger<VaultViewModel> logger,
        IMemoryService memoryService,
        IEmbeddingService embeddingService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        IClipboardService clipboardService,
        IVaultSourcesService vaultSourcesService,
        IIngestScheduler ingestScheduler,
        ISettingsService settingsService,
        IObsidianService obsidianService,
        ILintService lintService,
        ICharterDrafter charterDrafter,
        IVaultCharterService charterService)
    {
        _logger = logger;
        _memoryService = memoryService;
        _embeddingService = embeddingService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _clipboardService = clipboardService;
        _vaultSourcesService = vaultSourcesService;
        _ingestScheduler = ingestScheduler;
        _settingsService = settingsService;
        _obsidianService = obsidianService;
        _lintService = lintService;
        _charterDrafter = charterDrafter;
        _charterService = charterService;

        RefreshCommand = new AsyncRelayCommand(LoadMemoriesAsync);
        DeleteMemoryCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteDeleteMemory);
        EditMemoryCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteEditMemory);
        SaveEditCommand = new AsyncRelayCommand(ExecuteSaveEdit, CanSaveEdit);
        CancelEditCommand = new RelayCommand(ExecuteCancelEdit);
        DownloadEmbeddingModelCommand = new AsyncRelayCommand(ExecuteDownloadEmbeddingModel);
        RegenerateEmbeddingsCommand = new AsyncRelayCommand(ExecuteRegenerateEmbeddings);
        SelectMemoryCommand = new RelayCommand<VaultMemoryItem>(ExecuteSelectMemory);
        NavigateToLinkCommand = new AsyncRelayCommand<string>(ExecuteNavigateToLink);
        GoBackCommand = new AsyncRelayCommand(ExecuteGoBack, () => _backStack.Count > 0);
        CopyMarkdownCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteCopyMarkdown);
        ShowHelpCommand = new AsyncRelayCommand(ExecuteShowHelp);
        GoHomeCommand = new RelayCommand(ExecuteGoHome);
        OpenVaultFolderCommand = new RelayCommand(() => ShellLauncher.RevealInExplorer(_memoryService.VaultRoot));
        OpenVaultInObsidianCommand = new AsyncRelayCommand(ExecuteOpenVaultInObsidian);
        OpenNoteInObsidianCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteOpenNoteInObsidian);
        AddSourceFilesCommand = new AsyncRelayCommand<IReadOnlyList<string>>(ExecuteAddSourceFiles);
        RebuildPageCommand = new AsyncRelayCommand<VaultMemoryItem>(ExecuteRebuildPage);
        RebuildAllPagesCommand = new AsyncRelayCommand(ExecuteRebuildAllPages);
        CancelRebuildCommand = new RelayCommand(() => _rebuildCts?.Cancel());
        CleanUpVaultCommand = new AsyncRelayCommand(ExecuteCleanUpVault);
        DraftCharterCommand = new AsyncRelayCommand(ExecuteDraftCharter);
        SaveCharterCommand = new AsyncRelayCommand(ExecuteSaveCharter);
        EditCharterCommand = new RelayCommand(() => IsEditingCharter = true);
        CancelCharterEditCommand = new RelayCommand(ExecuteCancelCharterEdit);

        PropertyChanged += OnPropertyChanged;
        _ingestScheduler.IngestStarted += OnIngestStarted;
        _ingestScheduler.IngestCompleted += OnIngestCompleted;
    }

    // The scheduler raises on background threads; the VM is scoped while the scheduler is a
    // singleton, so Dispose MUST unsubscribe or these events pin the VM for the app lifetime.
    private void OnIngestCompleted(object? sender, EventArgs e)
    {
        // No context means we were constructed off the UI thread — refreshing here would mutate
        // ObservableCollections cross-thread, so skip; the next navigation reloads anyway.
        if (HasUiContext)
            Post(() => _ = LoadSourcesAsync());
    }

    // Ingest just started for a source: flip that row's spinner on WITHOUT a disk reload (cheap, so an
    // N-file reconcile doesn't rescan topic pages per item). The matching OnIngestCompleted does the
    // full reload, which clears the flag by reading the now-idle CurrentSourceRef.
    private void OnIngestStarted(object? sender, string sourceRef)
    {
        if (!HasUiContext)
            return;
        Post(() =>
        {
            foreach (var row in SourceFiles)
                row.IsIngesting =
                    string.Equals(row.RelativePath, sourceRef, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task ExecuteOpenVaultInObsidian()
    {
        var vaultRoot = _memoryService.VaultRoot;
        await PrepareObsidianOpenAsync(vaultRoot);
        _obsidianService.OpenVault(vaultRoot);
    }

    // The item's FilePath, never its Reference: a section address (path#heading) is not something Obsidian
    // resolves, and the whole file is what it opens anyway.
    private async Task ExecuteOpenNoteInObsidian(VaultMemoryItem? memory)
    {
        if (memory is null || !ObsidianLauncher.IsMarkdownNote(memory.FilePath)) return;
        var vaultRoot = _memoryService.VaultRoot;
        await PrepareObsidianOpenAsync(vaultRoot);
        _obsidianService.OpenNote(vaultRoot, memory.FilePath);
    }

    /// <summary>
    /// Obsidian has no API to register a vault it has never seen, only its own registry file — editing that
    /// while Obsidian runs races its own save and can corrupt every vault it lists, so registration is only
    /// ever attempted while it's closed, and only with consent. Every branch that ends without a registered
    /// vault hands the user the path to paste instead, rather than dropping them on Obsidian's vault
    /// switcher with no explanation. The caller's open call runs after this either way.
    /// </summary>
    private async Task PrepareObsidianOpenAsync(string vaultRoot)
    {
        switch (_obsidianService.GetRegistrationState(vaultRoot))
        {
            case VaultRegistrationState.Registered:
                return;

            // Nothing to offer: Pia cannot see the list Obsidian is reading, so it has no entry to merge
            // into and would only be guessing at where to write one.
            case VaultRegistrationState.Undetermined:
                await AdviseManualAddAsync(
                    vaultRoot,
                    _localizationService["Memory_ObsidianAddManually_Title"],
                    _localizationService["Memory_ObsidianAddManually_Body"]);
                return;
        }

        if (_obsidianService.IsObsidianRunning())
        {
            await AdviseManualAddAsync(
                vaultRoot,
                _localizationService["Memory_ObsidianAlreadyOpen_Title"],
                _localizationService["Memory_ObsidianAlreadyOpen_Body"]);
            return;
        }

        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.ObsidianVaultRegistrationConfirmSuppressed)
        {
            var answer = await _dialogService.ShowOptOutConfirmationDialogAsync(
                _localizationService["Memory_ObsidianRegisterConfirm_Title"],
                _localizationService["Memory_ObsidianRegisterConfirm_Body"],
                _localizationService["Memory_ObsidianRegisterConfirm_Confirm"]);

            if (!answer.Confirmed)
            {
                CopyVaultPathForPasting(vaultRoot);
                return;
            }

            if (answer.DontAskAgain)
            {
                settings.ObsidianVaultRegistrationConfirmSuppressed = true;
                await _settingsService.SaveSettingsAsync(settings);
            }
        }

        if (_obsidianService.TryRegisterVault(vaultRoot)) return;

        await AdviseManualAddAsync(
            vaultRoot,
            _localizationService["Memory_ObsidianRegisterFailed_Title"],
            _localizationService["Memory_ObsidianRegisterFailed_Body"]);
    }

    // The copy comes first: every one of these bodies tells the user the path is already on their clipboard.
    private async Task AdviseManualAddAsync(string vaultRoot, string title, string body)
    {
        CopyVaultPathForPasting(vaultRoot);
        await _dialogService.ShowMessageDialogAsync(title, body);
    }

    private void CopyVaultPathForPasting(string vaultRoot)
    {
        try
        {
            _clipboardService.SetText(vaultRoot);
        }
        catch (Exception ex)
        {
            // Obsidian still opens on its vault switcher; the user types the path instead of pasting it.
            _logger.LogWarning(ex, "Failed to copy the vault path to the clipboard for Obsidian");
        }
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
        // History is per view-entry — stale references must not linger across navigations.
        _backStack.Clear();
        GoBackCommand.NotifyCanExecuteChanged();
        IsEmbeddingModelAvailable = _embeddingService.IsModelAvailable;
        await LoadMemoriesAsync();

        // Deep-link from a chat's vault source chip: same resolve (and same not-found toast) as a wikilink.
        if (parameter is string target && !string.IsNullOrWhiteSpace(target))
        {
            await ExecuteNavigateToLink(target);
        }
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadMemoriesAsync()
    {
        try
        {
            IsLoading = true;

            // Not while the user is mid-edit — a background refresh would overwrite their draft.
            if (!IsEditingCharter)
            {
                CharterText = await _charterService.GetCharterAsync();
                CharterDraft = CharterText;
            }

            // One enumeration of the vault yields both the items and the storage size.
            var snapshot = await _memoryService.ListMemoriesAsync();
            var items = string.IsNullOrWhiteSpace(SearchQuery)
                ? snapshot.Items
                : FilterBySearch(snapshot.Items, await _memoryService.RecallAsync(SearchQuery), SearchQuery);

            var groups = BuildGroups(items);

            MemoryGroups.Clear();
            foreach (var group in groups)
            {
                MemoryGroups.Add(group);
            }

            // Keep the inspector on the selected memory only if it is still present (by reference). A
            // reload that drops the selection (e.g. a search filter hid it) is not a user navigation, so it
            // must not be recorded in the back history.
            if (SelectedMemory is not null &&
                !MemoryGroups.Any(g => g.Items.Any(m => m.Reference == SelectedMemory.Reference)))
            {
                SetSelectedMemorySilently(null);
                IsEditing = false;
            }

            // Header count is the total of displayable (canonical-typed) memories — independent of the
            // search filter — so it matches what the unfiltered grouped list shows (no silent divergence).
            TotalObjectCount = CountDisplayable(snapshot.Items);
            StorageSizeText = FormatBytes(snapshot.Bytes);

            // Composition is computed from the UNFILTERED snapshot (not the search-filtered `items`) so the
            // overview bar always agrees with the header total, even mid-search.
            BuildComposition(snapshot.Items);

            await LoadSourcesAsync();
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

    // Search combines two matchers so topic content is reachable: (1) the semantic recall over indexed
    // ## sections (structured docs), and (2) a plain case-insensitive substring match over every item's
    // title, category and BODY — freeform topic pages are not chunked by the indexer, so recall alone
    // never surfaces them (that was the reported bug). Results are unioned and de-duplicated by reference.
    private static IReadOnlyList<VaultMemoryItem> FilterBySearch(
        IReadOnlyList<VaultMemoryItem> all, IReadOnlyList<RecallHit> hits, string query)
    {
        var trimmed = query.Trim();
        var results = new List<VaultMemoryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in ProjectRecallHits(all, hits))
        {
            if (seen.Add(item.Reference))
            {
                results.Add(item);
            }
        }

        foreach (var item in all)
        {
            if ((ContainsIgnoreCase(item.Title, trimmed)
                    || ContainsIgnoreCase(item.Body, trimmed)
                    || ContainsIgnoreCase(item.Category, trimmed))
                && seen.Add(item.Reference))
            {
                results.Add(item);
            }
        }

        return results;
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

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

    // The §8 canonical type set (case-insensitive) — the types the Vault view can group and display.
    private static readonly HashSet<string> DisplayableTypes =
        new(VaultIndexService.CanonicalGroups.Select(g => g.Type), StringComparer.OrdinalIgnoreCase);

    // Count of memories the view can actually show, so the header total matches the grouped list rather
    // than counting hand-edited/foreign-typed records the canonical grouping silently drops.
    private static int CountDisplayable(IReadOnlyList<VaultMemoryItem> items)
        => items.Count(i => DisplayableTypes.Contains(i.Type));

    // The single canonical grouping walk shared by the left grouped list and the Vault Overview
    // composition, so the two can never drift (the overview once lumped all topics under one row while
    // the list already exploded them — this walk is the fix, and the fix against a recurrence). Yields
    // groups in §8 CanonicalGroups order, exploding the `topic` type into one group per frontmatter
    // `category` (TopicCategories order, "Other" bucket for missing/unknown). `Key` is the group's
    // identity: a canonical type for the fixed groups, or the topic category (e.g. "person") for an
    // exploded one — it also drives the overview swatch color (VaultCategoryColorConverter).
    private static IEnumerable<(string Key, string DisplayName, List<VaultMemoryItem> Items)>
        EnumerateDisplayGroups(IReadOnlyList<VaultMemoryItem> items)
    {
        // Case-insensitive so a case-drifted frontmatter `type` (e.g. "Note") still lands in its group.
        var byType = items
            .GroupBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (type, display) in VaultIndexService.CanonicalGroups)
        {
            if (!byType.TryGetValue(type, out var groupItems))
            {
                continue;
            }

            if (type == "topic")
            {
                var byCategory = groupItems
                    .GroupBy(i => VaultIndexService.NormalizeTopicCategory(i.Category), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

                foreach (var (category, categoryDisplay) in VaultIndexService.TopicCategories)
                {
                    if (byCategory.TryGetValue(category, out var categoryItems))
                    {
                        yield return (category, categoryDisplay, categoryItems);
                    }
                }

                continue;
            }

            yield return (type, display, groupItems);
        }
    }

    // Group by the §8 canonical type order with the spec's display names; within a group, items sort
    // alphabetically by title (D3: frontmatter `updated` is document-level, so per-item recency is
    // meaningless). The group timestamp is the newest document `updated` among its items.
    private static List<MemoryGroupViewModel> BuildGroups(IReadOnlyList<VaultMemoryItem> items)
    {
        var groups = new List<MemoryGroupViewModel>();
        foreach (var (key, display, groupItems) in EnumerateDisplayGroups(items))
        {
            groups.Add(BuildGroup(key, display, groupItems));
        }

        return groups;
    }

    private static MemoryGroupViewModel BuildGroup(
        string type, string display, IReadOnlyList<VaultMemoryItem> groupItems)
    {
        var ordered = groupItems
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new MemoryGroupViewModel
        {
            Type = type,
            DisplayName = display,
            Items = new ObservableCollection<VaultMemoryItem>(ordered),
            ItemCount = ordered.Count,
            LastUpdated = ordered.Max(i => i.Updated) ?? DateTime.MinValue,
        };
    }

    // Composition-by-category for the Vault Overview: one segment per group in the shared
    // EnumerateDisplayGroups walk, so the legend rows are the exact same set/order/counts as the left
    // group headers (topics exploded per category, not merged into one "Topics" row). Fraction = count /
    // totalDisplayable; `total` reuses CountDisplayable, so the bar and the header total agree by
    // construction. The segment's Type is the group Key (canonical type, or topic category) — the swatch
    // color derives from it (canonical → theme brush, topic category → cycled palette color).
    private void BuildComposition(IReadOnlyList<VaultMemoryItem> items)
    {
        var total = CountDisplayable(items);

        VaultComposition.Clear();
        if (total == 0)
        {
            return; // Divide-by-zero guard: an empty (or all-foreign-typed) vault emits no segments.
        }

        foreach (var (key, display, groupItems) in EnumerateDisplayGroups(items))
        {
            var count = groupItems.Count;
            VaultComposition.Add(new VaultCategorySegment(key, display, count, count / (double)total));
        }
    }

    // Rebuild the overview's "Source documents" rows from the sources/ RAW layer. Rows are
    // display-ready: the ingest-status line and sizes are formatted here so XAML binds plain strings.
    private async Task LoadSourcesAsync()
    {
        var sources = await _vaultSourcesService.ListSourcesAsync();

        // Authoritative running ref, so a view opened DURING an ingest (e.g. the startup reconcile)
        // shows the spinner even though it never observed the IngestStarted event.
        var running = _ingestScheduler.CurrentSourceRef;

        SourceFiles.Clear();
        foreach (var source in sources)
        {
            var status = source.IsIngested
                ? _localizationService.Format("Memory_Sources_IngestedPages", source.TopicPageCount)
                : _localizationService[source.IsText ? "Memory_Sources_NotIngested" : "Memory_Sources_NotText"];
            SourceFiles.Add(new VaultSourceRow(
                source.Name, source.RelativePath, source.IsIngested, status, FormatBytes(source.Bytes))
            {
                IsIngesting = string.Equals(source.RelativePath, running, StringComparison.OrdinalIgnoreCase),
            });
        }

        SourceFileCount = sources.Count;
        SourcesSummaryText = _localizationService.Format(
            "Memory_Sources_Summary", sources.Count, FormatBytes(sources.Sum(s => s.Bytes)));
    }

    // Drag-and-drop entry point (the overview is the drop target): copy each dropped TEXT file into the
    // vault's sources/ folder, surface the new rows immediately, then kick a manual ingest per file. The
    // manual RunAsync always executes (unlike the hash-gated watcher) and, being on the same serial queue,
    // never races the watcher's auto-run of the same copy — whichever wins records the hash and the other
    // no-ops. Non-text files are silently skipped (the drop target already discourages them).
    private async Task ExecuteAddSourceFiles(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return;

        var sourcesDir = Path.Combine(_memoryService.VaultRoot, "sources");

        IReadOnlyList<string> added;
        try
        {
            // File copies can be multi-MB (a .log/.csv), so never run them on the drop (UI) thread.
            added = await Task.Run(() => CopyTextSources(paths, sourcesDir));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stage dropped source files");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_SourcesAddFailed", ex.Message));
            return;
        }

        if (added.Count == 0)
        {
            // Everything dropped was a folder or a non-text file — say so rather than silently no-op.
            _snackbarService.Show(
                _localizationService["Msg_Memory_SourcesNoTextTitle"],
                _localizationService["Msg_Memory_SourcesNoText"],
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        // Show the staged files as not-yet-ingested rows before the (slower) LLM compile begins.
        await LoadSourcesAsync();

        foreach (var sourceRef in added)
            _ingestScheduler.RunAsync(sourceRef).SafeFireAndForget(_logger);

        _snackbarService.Show(
            _localizationService["Msg_Memory_SourcesAdded"],
            _localizationService.Format("Msg_Memory_SourcesAddedDetail", added.Count),
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    // Copy the text-typed dropped files into sources/, returning the vault-relative refs of what landed.
    // Directories and non-text files are skipped; a name that already exists is uniquified rather than
    // overwritten so a drop can never clobber a previously staged source.
    private static IReadOnlyList<string> CopyTextSources(IReadOnlyList<string> paths, string sourcesDir)
    {
        Directory.CreateDirectory(sourcesDir);
        var added = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !SourcesProvenance.IsTextSource(path))
                continue;

            var dest = UniqueDestination(sourcesDir, Path.GetFileName(path));
            File.Copy(path, dest);
            added.Add("sources/" + Path.GetFileName(dest));
        }
        return added;
    }

    private static string UniqueDestination(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest))
            return dest;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
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
                // Deleting the viewed page returns to the overview; a deleted page is not a valid Back
                // target, so don't record it.
                SetSelectedMemorySilently(null);
                IsEditing = false;
            }

            // Purge any history entries pointing at the deleted page (and its sections) so Back never
            // targets a dead reference (which would otherwise trigger a surprising search-clear + reload).
            if (_backStack.RemoveAll(r =>
                    string.Equals(r, memory.Reference, StringComparison.OrdinalIgnoreCase)
                    || r.StartsWith(memory.Reference + "#", StringComparison.OrdinalIgnoreCase)) > 0)
            {
                GoBackCommand.NotifyCanExecuteChanged();
            }

            var snapshot = await _memoryService.ListMemoriesAsync();
            TotalObjectCount = CountDisplayable(snapshot.Items);
            StorageSizeText = FormatBytes(snapshot.Bytes);
            BuildComposition(snapshot.Items);

            // Deleting a topic page removes its `sources:` provenance, so the source rows refresh too.
            await LoadSourcesAsync();

            _snackbarService.Show(_localizationService["Msg_Memory_Deleted"], _localizationService.Format("Msg_Memory_MemoryDeleted", memory.Title),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete memory");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_DeleteFailed", ex.Message));
        }
    }

    private async Task ExecuteRebuildPage(VaultMemoryItem? memory)
    {
        if (memory is null || !memory.IsRebuildable || IsRebuilding) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Memory_RebuildTitle"],
            _localizationService.Format("Msg_Memory_RebuildConfirm", memory.Title));

        if (!confirmed) return;

        _rebuildCts = new CancellationTokenSource();
        IsRebuilding = true;
        RebuildStatusText = _localizationService.Format("Memory_Rebuild_Progress", 1, 1);
        try
        {
            var rebuilt = await _ingestScheduler.RebuildPageAsync(memory.FilePath, _rebuildCts.Token);
            await LoadMemoriesAsync();
            ReselectByReference(memory.Reference);

            if (rebuilt)
            {
                _snackbarService.Show(
                    _localizationService["Msg_Memory_Rebuilt"],
                    _localizationService.Format("Msg_Memory_RebuiltDetail", memory.Title),
                    Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
            else
            {
                // No sources recorded, or synthesis came back empty — the page is untouched, not broken.
                _snackbarService.Show(
                    _localizationService["Msg_Memory_RebuildSkippedTitle"],
                    _localizationService["Msg_Memory_RebuildSkipped"],
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(5));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user — nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild a topic page");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_RebuildFailed", ex.Message));
        }
        finally
        {
            EndRebuild();
        }
    }

    // The draft is never written — it lands in the editor for the user to accept, edit or discard.
    private async Task ExecuteDraftCharter()
    {
        if (IsDraftingCharter) return;

        IsDraftingCharter = true;
        try
        {
            var draft = await _charterDrafter.DraftAsync();
            if (string.IsNullOrWhiteSpace(draft))
            {
                _snackbarService.Show(
                    _localizationService["Memory_Charter"],
                    _localizationService["Msg_Memory_CharterDraftEmpty"],
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                return;
            }

            CharterDraft = draft;
            IsEditingCharter = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Charter draft failed");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_CharterDraftFailed", ex.Message));
        }
        finally
        {
            IsDraftingCharter = false;
        }
    }

    private async Task ExecuteSaveCharter()
    {
        try
        {
            await _charterService.SaveCharterAsync(CharterDraft);
            CharterText = CharterDraft.Trim();
            IsEditingCharter = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the vault charter failed");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_CharterSaveFailed", ex.Message));
        }
    }

    private void ExecuteCancelCharterEdit()
    {
        CharterDraft = CharterText;
        IsEditingCharter = false;
    }

    // Dry run first, always. The pass merges pages and rewrites bodies, so the user sees the exact
    // list before anything moves — and a merge costs a synthesis call per merged page.
    private async Task ExecuteCleanUpVault()
    {
        if (IsRebuilding) return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        LintReport preview;
        IsRebuilding = true;
        RebuildStatusText = _localizationService["Memory_CleanUp_Scanning"];
        try
        {
            preview = await _lintService.RunAsync(today, applyFixes: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault clean-up dry run failed");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_CleanUpFailed", ex.Message));
            return;
        }
        finally
        {
            EndRebuild();
        }

        var fixable = preview.Findings
            .Where(f => f.Kind is LintKind.Duplicate or LintKind.MissingXref)
            .ToList();
        if (fixable.Count == 0)
        {
            _snackbarService.Show(
                _localizationService["Memory_CleanUp"],
                _localizationService["Msg_Memory_CleanUpNothingToDo"],
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            return;
        }

        var merges = fixable.Count(f => f.Kind == LintKind.Duplicate);
        var links = fixable.Count - merges;
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Memory_CleanUp"],
            _localizationService.Format("Msg_Memory_CleanUpConfirm", merges, links));
        if (!confirmed) return;

        IsRebuilding = true;
        RebuildStatusText = _localizationService["Memory_CleanUp_Applying"];
        try
        {
            var applied = await _lintService.RunAsync(today, applyFixes: true);
            var done = applied.Findings.Count(f => f.AutoFixed);
            _snackbarService.Show(
                _localizationService["Memory_CleanUp"],
                _localizationService.Format("Msg_Memory_CleanUpDone", done),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault clean-up failed");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_CleanUpFailed", ex.Message));
        }
        finally
        {
            EndRebuild();
        }

        await LoadMemoriesAsync();
    }

    private async Task ExecuteRebuildAllPages()
    {
        if (IsRebuilding) return;

        IReadOnlyList<string> pages;
        try
        {
            pages = await _ingestScheduler.ListTopicPagesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list topic pages for a bulk rebuild");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_RebuildFailed", ex.Message));
            return;
        }

        if (pages.Count == 0)
        {
            _snackbarService.Show(
                _localizationService["Msg_Memory_RebuildSkippedTitle"],
                _localizationService["Msg_Memory_RebuildNoPages"],
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Memory_RebuildTitle"],
            _localizationService.Format("Msg_Memory_RebuildAllConfirm", pages.Count));

        if (!confirmed) return;

        _rebuildCts = new CancellationTokenSource();
        IsRebuilding = true;
        var rebuilt = 0;
        var skipped = 0;
        var failed = false;
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                _rebuildCts.Token.ThrowIfCancellationRequested();
                RebuildStatusText = _localizationService.Format("Memory_Rebuild_Progress", i + 1, pages.Count);

                if (await _ingestScheduler.RebuildPageAsync(pages[i], _rebuildCts.Token))
                {
                    rebuilt++;
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-run: pages already rebuilt keep their new body, the rest are untouched.
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogError(ex, "Bulk rebuild of topic pages failed");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_Memory_RebuildFailed", ex.Message));
        }
        finally
        {
            EndRebuild();
        }

        // Pages rebuilt before a cancel or a failure are already on disk, so reload either way — but
        // don't follow an error dialog with a success toast.
        await LoadMemoriesAsync();
        if (!failed)
        {
            _snackbarService.Show(
                _localizationService["Msg_Memory_Rebuilt"],
                _localizationService.Format("Msg_Memory_RebuiltAllDetail", rebuilt, skipped),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(5));
        }
    }

    private void EndRebuild()
    {
        IsRebuilding = false;
        RebuildStatusText = string.Empty;
        _rebuildCts?.Dispose();
        _rebuildCts = null;
    }

    // Re-select a page by reference after a reload replaced every item instance.
    private void ReselectByReference(string reference)
    {
        foreach (var group in MemoryGroups)
        {
            var match = group.Items.FirstOrDefault(m => m.Reference == reference);
            if (match is not null)
            {
                SelectedMemory = match;
                return;
            }
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
            ReselectByReference(reference);

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

    // Follow an in-app wikilink (rewritten from `[[target]]` in the inspector) to its vault page. The target
    // is a §5 link target (path-without-ext, e.g. `topics/foo`); VaultIndexService maps it to candidate
    // references, including a slug-normalized one so a synthesized link whose slug drifts from the on-disk
    // filename still resolves. A structured topic (with `## sections`) resolves to its first section. If an
    // active search has filtered the target out, the search is cleared and the full vault reloaded before
    // retrying; a target with no page on disk (an unresolved Obsidian link) surfaces a snackbar.
    private async Task ExecuteNavigateToLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        // A programmatic navigation supersedes a search the user typed but hasn't committed — cancel the
        // pending debounce so it can't re-filter and silently revert this navigation ~500ms later.
        _debounceCts?.Cancel();

        var match = await EnsureLoadedAsync(() => ResolveWikiTarget(target));
        if (match is null)
        {
            _snackbarService.Show(
                _localizationService.Format("Memory_LinkNotFound", target.Trim().Trim('/')), string.Empty,
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        IsEditing = false;
        SelectedMemory = match;
    }

    // Browser-style Back: pop the most-recent history entries until one resolves to a loaded (or reloadable)
    // page, then select it WITHOUT re-recording. Entries whose page has since been deleted are skipped.
    private async Task ExecuteGoBack()
    {
        // As with link navigation, an in-flight uncommitted search must not revert where Back lands.
        _debounceCts?.Cancel();

        while (_backStack.Count > 0)
        {
            var reference = _backStack[^1];
            _backStack.RemoveAt(_backStack.Count - 1);

            // Resolve WITHOUT holding suppression across the await: only the final selection is silent, so a
            // user action during the (rare) search-clear reload is still recorded normally rather than lost.
            var match = await EnsureLoadedAsync(() => FindByReference(reference));
            if (match is not null)
            {
                IsEditing = false;
                SetSelectedMemorySilently(match);
                break;
            }
        }

        GoBackCommand.NotifyCanExecuteChanged();
    }

    // Record browser-style history on every page change (link click, list click, or Home → overview),
    // except the programmatic re-selection GoBack performs. Recording page → overview too means Back after
    // Home returns to the page you left.
    partial void OnSelectedMemoryChanged(VaultMemoryItem? oldValue, VaultMemoryItem? newValue)
    {
        if (_suppressHistory || oldValue is null || oldValue.Reference == newValue?.Reference)
        {
            return;
        }

        _backStack.Add(oldValue.Reference);
        if (_backStack.Count > MaxHistory)
        {
            _backStack.RemoveAt(0);
        }
        GoBackCommand.NotifyCanExecuteChanged();
    }

    // Change the selection without recording history — for programmatic (reload/delete) deselections that
    // are not user navigations. Re-entrant-safe: restores the prior suppression state so it composes with
    // GoBack's broader suppression.
    private void SetSelectedMemorySilently(VaultMemoryItem? value)
    {
        var previous = _suppressHistory;
        _suppressHistory = true;
        try
        {
            SelectedMemory = value;
        }
        finally
        {
            _suppressHistory = previous;
        }
    }

    // Resolve via the supplied resolver against the loaded groups; if the target is hidden by an active
    // search filter, clear the search and reload the full vault, then resolve again.
    private async Task<VaultMemoryItem?> EnsureLoadedAsync(Func<VaultMemoryItem?> resolve)
    {
        var match = resolve();
        if (match is null && !string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchQuery = string.Empty;
            _debounceCts?.Cancel();
            await LoadMemoriesAsync();
            match = resolve();
        }
        return match;
    }

    // First loaded item matching any candidate reference for the wikilink target (exact or slug-normalized).
    private VaultMemoryItem? ResolveWikiTarget(string target)
    {
        foreach (var reference in VaultIndexService.WikiTargetReferences(target))
        {
            var match = FindByReference(reference);
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    // First loaded item whose reference is the bare page path or one of its `path#heading` sections.
    private VaultMemoryItem? FindByReference(string reference) =>
        MemoryGroups
            .SelectMany(g => g.Items)
            .FirstOrDefault(i =>
                string.Equals(i.Reference, reference, StringComparison.OrdinalIgnoreCase)
                || i.Reference.StartsWith(reference + "#", StringComparison.OrdinalIgnoreCase));

    // Home: return to the "vault at a glance" overview by clearing the current selection (and any edit).
    // The search query is intentionally left intact — the overview reappears from the null-selection state.
    private void ExecuteGoHome()
    {
        SelectedMemory = null;
        IsEditing = false;
    }

    // Help is a dialog overlay (not an inline card that reflows the page); the vault root feeds its
    // "open folder" affordance.
    private Task ExecuteShowHelp() => _dialogService.ShowMemoryHelpDialogAsync(_memoryService.VaultRoot);

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
        _rebuildCts?.Cancel();
        _rebuildCts?.Dispose();
        PropertyChanged -= OnPropertyChanged;
        _ingestScheduler.IngestStarted -= OnIngestStarted;
        _ingestScheduler.IngestCompleted -= OnIngestCompleted;

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
