using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Helpers;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class MemoryViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly ILogger<MemoryViewModel> _logger;
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<MemoryGroupViewModel> _memoryGroups = new();

    [ObservableProperty]
    private MemoryObject? _selectedMemory;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalObjectCount;

    [ObservableProperty]
    private string _storageSizeText = "0 B";

    [ObservableProperty]
    private bool _isEmbeddingModelAvailable;

    [ObservableProperty]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private float _downloadProgress;

    [ObservableProperty]
    private string _selectedMemoryDataFormatted = string.Empty;

    [ObservableProperty]
    private string _editingData = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<MemoryObject> DeleteMemoryCommand { get; }
    public IAsyncRelayCommand<MemoryObject> EditMemoryCommand { get; }
    public IAsyncRelayCommand SaveEditCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand DownloadEmbeddingModelCommand { get; }
    public IAsyncRelayCommand RegenerateEmbeddingsCommand { get; }
    public IAsyncRelayCommand ReviewStaleCommand { get; }
    public IRelayCommand<MemoryObject> SelectMemoryCommand { get; }

    public MemoryViewModel(
        ILogger<MemoryViewModel> logger,
        IMemoryService memoryService,
        IEmbeddingService embeddingService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService)
    {
        _logger = logger;
        _memoryService = memoryService;
        _embeddingService = embeddingService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;

        RefreshCommand = new AsyncRelayCommand(LoadMemoriesAsync);
        DeleteMemoryCommand = new AsyncRelayCommand<MemoryObject>(ExecuteDeleteMemory);
        EditMemoryCommand = new AsyncRelayCommand<MemoryObject>(ExecuteEditMemory);
        SaveEditCommand = new AsyncRelayCommand(ExecuteSaveEdit, CanSaveEdit);
        CancelEditCommand = new RelayCommand(ExecuteCancelEdit);
        ExportCommand = new AsyncRelayCommand(ExecuteExport);
        DownloadEmbeddingModelCommand = new AsyncRelayCommand(ExecuteDownloadEmbeddingModel);
        RegenerateEmbeddingsCommand = new AsyncRelayCommand(ExecuteRegenerateEmbeddings);
        ReviewStaleCommand = new AsyncRelayCommand(ExecuteReviewStale);
        SelectMemoryCommand = new RelayCommand<MemoryObject>(ExecuteSelectMemory);

        PropertyChanged += OnPropertyChanged;
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

            IReadOnlyList<MemoryObject> memories;

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                memories = await _memoryService.HybridSearchAsync(SearchQuery);
            }
            else
            {
                memories = await _memoryService.GetAllObjectsAsync();
            }

            // Group by type
            var groups = memories
                .GroupBy(m => m.Type)
                .OrderBy(g => g.Key)
                .Select(g => new MemoryGroupViewModel
                {
                    Type = g.Key,
                    DisplayName = MemoryObjectTypes.GetDisplayName(g.Key),
                    Items = new ObservableCollection<MemoryObject>(g.OrderByDescending(m => m.UpdatedAt)),
                    ItemCount = g.Count(),
                    LastUpdated = g.Max(m => m.UpdatedAt)
                })
                .ToList();

            MemoryGroups.Clear();
            foreach (var group in groups)
            {
                MemoryGroups.Add(group);
            }

            TotalObjectCount = await _memoryService.GetObjectCountAsync();
            var storageSize = await _memoryService.GetStorageSizeAsync();
            StorageSizeText = FormatBytes(storageSize);
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

    private async Task ExecuteDeleteMemory(MemoryObject? memory)
    {
        if (memory is null) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Memory_DeleteTitle"],
            _localizationService.Format("Msg_Memory_DeleteConfirm", memory.Label));

        if (!confirmed) return;

        try
        {
            await _memoryService.DeleteObjectAsync(memory.Id);

            // Remove from the group
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

            TotalObjectCount = await _memoryService.GetObjectCountAsync();
            var storageSize = await _memoryService.GetStorageSizeAsync();
            StorageSizeText = FormatBytes(storageSize);

            _snackbarService.Show(_localizationService["Msg_Memory_Deleted"], _localizationService.Format("Msg_Memory_MemoryDeleted", memory.Label),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete memory {Id}", memory.Id);
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_DeleteFailed", ex.Message));
        }
    }

    private Task ExecuteEditMemory(MemoryObject? memory)
    {
        if (memory is null) return Task.CompletedTask;

        SelectedMemory = memory;
        EditingData = JsonHelper.FormatJson(memory.Data);
        IsEditing = true;
        SaveEditCommand.NotifyCanExecuteChanged();

        return Task.CompletedTask;
    }

    private bool CanSaveEdit() => IsEditing && SelectedMemory is not null;

    private async Task ExecuteSaveEdit()
    {
        if (SelectedMemory is null) return;

        try
        {
            // Validate JSON
            JsonNode.Parse(EditingData);

            // Use the full replacement as a merge patch (replace all data)
            var connection = SelectedMemory;
            await _memoryService.UpdateObjectAsync(connection.Id, EditingData);

            // Regenerate embedding if model is available
            if (_embeddingService.IsModelAvailable)
            {
                try
                {
                    var textToEmbed = $"{connection.Label} {EditingData}";
                    var embedding = await _embeddingService.GenerateEmbeddingAsync(textToEmbed);
                    await _memoryService.UpdateEmbeddingAsync(connection.Id, _embeddingService.FloatsToBytes(embedding));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to regenerate embedding for {Id}", connection.Id);
                }
            }

            IsEditing = false;

            // Refresh to show updated data
            await LoadMemoriesAsync();

            _snackbarService.Show(_localizationService["Msg_Memory_Saved"], _localizationService["Msg_Memory_MemoryUpdated"],
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (JsonException)
        {
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Memory_InvalidJsonTitle"], _localizationService["Msg_Memory_InvalidJsonMessage"]);
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

    private void ExecuteSelectMemory(MemoryObject? memory)
    {
        if (memory is not null)
        {
            SelectedMemory = memory;
        }
    }

    private async Task ExecuteExport()
    {
        try
        {
            var exportJson = await _memoryService.ExportAllAsync();

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var exportDir = Path.Combine(localAppData, "Pia", "Exports");
            Directory.CreateDirectory(exportDir);

            var fileName = $"pia-memories-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";
            var exportPath = Path.Combine(exportDir, fileName);
            await File.WriteAllTextAsync(exportPath, exportJson);

            _snackbarService.Show(_localizationService["Msg_Memory_Exported"], _localizationService.Format("Msg_Memory_ExportedTo", exportPath),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export memories");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Memory_ExportFailed", ex.Message));
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

    private async Task ExecuteReviewStale()
    {
        try
        {
            var staleMemories = await _memoryService.GetStaleObjectsAsync(TimeSpan.FromDays(90));

            if (staleMemories.Count == 0)
            {
                _snackbarService.Show(_localizationService["Msg_Memory_AllFresh"], _localizationService["Msg_Memory_NoStaleMemories"],
                    Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
                return;
            }

            var message = _localizationService.Format("Msg_Memory_StaleFound", staleMemories.Count) + "\n\n";
            message += string.Join("\n", staleMemories.Take(10).Select(m =>
                $"- {m.Label} ({MemoryObjectTypes.GetDisplayName(m.Type)}) - {_localizationService.Format("Msg_Memory_LastAccessed", m.LastAccessedAt.ToString("yyyy-MM-dd"))}"));

            if (staleMemories.Count > 10)
            {
                message += "\n" + _localizationService.Format("Msg_Memory_AndMore", staleMemories.Count - 10);
            }

            message += "\n\n" + _localizationService["Msg_Memory_DeleteAllStaleQuestion"];

            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                _localizationService["Msg_Memory_StaleReviewTitle"], message);

            if (confirmed)
            {
                foreach (var memory in staleMemories)
                {
                    await _memoryService.DeleteObjectAsync(memory.Id);
                }

                await LoadMemoriesAsync();

                _snackbarService.Show(_localizationService["Msg_Memory_CleanedUp"], _localizationService.Format("Msg_Memory_StaleDeleted", staleMemories.Count),
                    Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to review stale memories");
        }
    }

    private void DebounceSearch()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        SafeFireAndForget(DebounceAsync(500, LoadMemoriesAsync, token));
    }

    private static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchQuery))
        {
            DebounceSearch();
        }

        if (e.PropertyName == nameof(SelectedMemory) && SelectedMemory is not null)
        {
            SelectedMemoryDataFormatted = JsonHelper.FormatJson(SelectedMemory.Data);
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
    private ObservableCollection<MemoryObject> _items = new();

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private DateTime _lastUpdated;

    [ObservableProperty]
    private bool _isExpanded;
}
