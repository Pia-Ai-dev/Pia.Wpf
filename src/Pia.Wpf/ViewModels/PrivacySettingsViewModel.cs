using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using System.Collections.ObjectModel;

namespace Pia.ViewModels;

/// <summary>
/// PII tokenization and private-keyword settings. Lives under General because it applies to all
/// AI traffic regardless of cloud sign-in. Extracted from <see cref="AccountSettingsViewModel"/>.
/// </summary>
public partial class PrivacySettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private bool _isLoading;
    private bool _disposed;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }

    public PrivacySettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        IPolicyService policyService)
    {
        _logger = logger;
        _settingsService = settingsService;
        Policy = new PolicyLock(policyService);

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Policy.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    private bool _tokenizationEnabled;

    [ObservableProperty]
    private ObservableCollection<PiiKeywordEntry> _piiKeywords = new();

    [ObservableProperty]
    private string _newKeywordInput = string.Empty;

    [ObservableProperty]
    private string _selectedNewCategory = "Custom";

    public List<string> AvailableCategories { get; } = ["Person", "Nickname", "Email", "Phone", "Address", "Date", "Custom"];

    partial void OnTokenizationEnabledChanged(bool value)
    {
        if (!_isLoading) SavePrivacySettingsAsync().SafeFireAndForget(_logger);
    }

    public async Task InitializeAsync()
    {
        // Set before the await so a click landing mid-load cannot save the defaults.
        _isLoading = true;
        ApplySettings(await _settingsService.GetSettingsAsync());
    }

    // Raised from the policy pull thread, so the mirror has to be marshalled.
    private void OnSettingsChanged(object? sender, AppSettings settings) => Post(() => ApplySettings(settings));

    private void ApplySettings(AppSettings settings)
    {
        _isLoading = true;

        TokenizationEnabled = settings.Privacy.TokenizationEnabled;
        var entries = settings.Privacy.PiiKeywords;
        // Replacing the bound collection resets the list under a user mid-edit, so only when it moved.
        if (!PiiKeywords.Select(e => (e.Keyword, e.Category))
                .SequenceEqual(entries.Select(e => (e.Keyword, e.Category))))
        {
            foreach (var entry in PiiKeywords)
                entry.PropertyChanged -= OnPiiKeywordEntryChanged;
            foreach (var entry in entries)
                entry.PropertyChanged += OnPiiKeywordEntryChanged;
            PiiKeywords = new ObservableCollection<PiiKeywordEntry>(entries);
        }

        _isLoading = false;
    }

    [RelayCommand]
    private async Task AddPiiKeywordAsync()
    {
        var keyword = NewKeywordInput?.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || PiiKeywords.Any(e => string.Equals(e.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
            return;

        var entry = new PiiKeywordEntry { Keyword = keyword, Category = SelectedNewCategory };
        entry.PropertyChanged += OnPiiKeywordEntryChanged;
        PiiKeywords.Add(entry);
        NewKeywordInput = string.Empty;
        await SavePrivacySettingsAsync();
    }

    [RelayCommand]
    private async Task RemovePiiKeywordAsync(PiiKeywordEntry entry)
    {
        entry.PropertyChanged -= OnPiiKeywordEntryChanged;
        if (PiiKeywords.Remove(entry))
            await SavePrivacySettingsAsync();
    }

    private void OnPiiKeywordEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_isLoading && e.PropertyName == nameof(PiiKeywordEntry.Category))
            SavePrivacySettingsAsync().SafeFireAndForget(_logger);
    }

    private async Task SavePrivacySettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.Privacy.TokenizationEnabled = TokenizationEnabled;
        settings.Privacy.PiiKeywords = PiiKeywords.Select(e => new PiiKeywordEntry { Keyword = e.Keyword, Category = e.Category }).ToList();
        await _settingsService.SaveSettingsAsync(settings);
    }
}
