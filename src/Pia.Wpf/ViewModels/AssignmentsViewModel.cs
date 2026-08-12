using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>The server knows what state a run is in; only this device knows what was asked and which chat holds
/// the answer, so the list is one joined to the other.</summary>
public partial class AssignmentsViewModel : UiThreadViewModel, INavigationAware, IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private const int PageSize = 50;

    private readonly IAssignmentApiClient _api;
    private readonly IAssignmentPendingStore _pending;
    private readonly IAssignmentRunOrchestrator _orchestrator;
    private readonly IDialogService _dialogService;
    private readonly IWindowManagerService _windowManager;
    private readonly ILocalizationService _localization;
    private readonly Func<AssignmentConsentViewModel> _consentFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AssignmentsViewModel> _logger;
    private readonly Dictionary<string, string> _skillDisplayNames = [];

    /// <summary>Created inert and armed on arrival, so the view's comings and goings never leak a second one.</summary>
    private readonly ITimer _timer;

    private AssignmentSurface _surface = AssignmentSurface.Hidden;
    private bool _isPolling;
    private bool _hasAnswered;
    private bool _noticeIsTransport;
    private bool _disposed;

    public AssignmentsViewModel(
        IAssignmentApiClient api,
        IAssignmentPendingStore pending,
        IAssignmentRunOrchestrator orchestrator,
        IDialogService dialogService,
        IWindowManagerService windowManager,
        ILocalizationService localization,
        Func<AssignmentConsentViewModel> consentFactory,
        TimeProvider time,
        ILogger<AssignmentsViewModel> logger)
    {
        _api = api;
        _pending = pending;
        _orchestrator = orchestrator;
        _dialogService = dialogService;
        _windowManager = windowManager;
        _localization = localization;
        _consentFactory = consentFactory;
        _time = time;
        _logger = logger;
        _timer = time.CreateTimer(_ => _ = TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public ObservableCollection<AssignmentRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewAssignmentCommand))]
    private bool _canStartAssignment;

    /// <summary>"Nothing has run yet" is a claim about the server's answer, so it waits for one.</summary>
    public bool IsEmpty => !IsLoading && _hasAnswered && Rows.Count == 0;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public void OnNavigatedTo(object? parameter) { }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _isPolling = true;
        IsLoading = Rows.Count == 0;

        try
        {
            await LoadSurfaceAsync();
            await RefreshAsync();
        }
        finally
        {
            IsLoading = false;
        }

        // Re-checked after the awaits: the view may have been hidden while the first load was in flight.
        if (_isPolling) _timer.Change(PollInterval, PollInterval);
    }

    public void OnNavigatedFrom() => StopPolling();

    /// <summary>The poll's whole body, so a test can drive a tick without waiting on a clock.</summary>
    internal async Task TickAsync()
    {
        if (!_isPolling) return;

        var snapshot = await FetchAsync();
        if (!_isPolling) return;

        await ApplyAsync(snapshot);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ApplyAsync(await FetchAsync());

    [RelayCommand(CanExecute = nameof(CanStartAssignment))]
    private async Task NewAssignmentAsync()
    {
        SetNotice(string.Empty);
        var consent = _consentFactory();

        try
        {
            await consent.InitializeAsync(_surface);
            if (!await _dialogService.ShowAssignmentConsentDialogAsync(consent)) return;

            await consent.SendAsync();
            SetNotice(consent.ResultMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The background-assignment dialog could not be completed.");
            SetNotice(_localization["AssignmentConsent_Result_Error"]);
        }

        await RefreshAsync();
    }

    private async Task LoadSurfaceAsync()
    {
        try
        {
            _surface = await _api.GetSurfaceAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read the background-assignment surface.");
            _surface = AssignmentSurface.Hidden;
        }

        _skillDisplayNames.Clear();
        foreach (var skill in _surface.Skills) _skillDisplayNames[skill.Name] = skill.DisplayName;
        CanStartAssignment = _surface.Available;
    }

    /// <summary>Null is "the server did not answer", which must never reach <see cref="ApplyRows"/> — an
    /// unanswered read there would wipe every row and claim nothing has run.</summary>
    private async Task<(IReadOnlyList<AssignmentDto> Server, IReadOnlyList<PendingAssignment> Journal)?> FetchAsync()
    {
        try
        {
            var server = await _api.ListAsync(0, PageSize);
            if (server is null) return null;

            var journal = await _pending.GetJournalAsync();
            return (server, journal);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not refresh the background-assignment list.");
            return null;
        }
    }

    private async Task ApplyAsync(
        (IReadOnlyList<AssignmentDto> Server, IReadOnlyList<PendingAssignment> Journal)? snapshot)
    {
        if (snapshot is null)
        {
            await PostAsync(SetTransportNotice);
            return;
        }

        await PostAsync(() =>
        {
            ClearTransportNotice();
            _hasAnswered = true;
            ApplyRows(snapshot.Value.Server, snapshot.Value.Journal);
        });
    }

    /// <summary>Whatever the action the user just took had to say outranks this.</summary>
    private void SetTransportNotice()
    {
        if (HasNotice && !_noticeIsTransport) return;
        SetNotice(_localization["Assignments_Refresh_Failed"], fromTransport: true);
    }

    private void ClearTransportNotice()
    {
        if (_noticeIsTransport) SetNotice(string.Empty);
    }

    private void SetNotice(string message, bool fromTransport = false)
    {
        Notice = message;
        _noticeIsTransport = fromTransport;
    }

    /// <summary>Rows come from the SERVER's page; the journal only enriches one. A local entry with no server row
    /// is a run nothing answers for any more.</summary>
    private void ApplyRows(IReadOnlyList<AssignmentDto> server, IReadOnlyList<PendingAssignment> journal)
    {
        var journalById = new Dictionary<Guid, PendingAssignment>();
        foreach (var entry in journal) journalById[entry.AssignmentId] = entry;

        var live = server.Select(d => d.Id).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(Rows[i].Id)) Rows.RemoveAt(i);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < server.Count; i++)
        {
            var dto = server[i];
            journalById.TryGetValue(dto.Id, out var entry);
            var displayName = _skillDisplayNames.TryGetValue(dto.SkillName, out var name) ? name : dto.SkillName;
            var elapsed = ElapsedOf(dto, now);

            var existing = Rows.FirstOrDefault(r => r.Id == dto.Id);
            if (existing is null)
            {
                Rows.Insert(
                    i,
                    new AssignmentRowViewModel(
                        dto, entry, displayName, elapsed, _localization, OpenChat, CancelRowAsync));
                continue;
            }

            existing.Apply(dto, entry, displayName, elapsed);
            var at = Rows.IndexOf(existing);
            if (at != i) Rows.Move(at, i);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OpenChat(AssignmentRowViewModel row)
    {
        if (row.ChatId is not { } chatId) return;
        _windowManager.ShowAssistantChat(chatId);
    }

    private async Task CancelRowAsync(AssignmentRowViewModel row)
    {
        SetNotice(string.Empty);

        try
        {
            var stopped = await _orchestrator.CancelAsync(row.Id);
            SetNotice(stopped
                ? _localization["Assignments_Cancel_Requested"]
                : _localization["Assignments_Cancel_NothingToStop"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not cancel assignment {AssignmentId}.", row.Id);
            SetNotice(_localization["Assignments_Cancel_Failed"]);
        }

        await RefreshAsync();
    }

    /// <summary>The wire's timestamps are read as UTC because System.Text.Json only tags them so when the JSON
    /// carried a <c>Z</c>.</summary>
    private static TimeSpan ElapsedOf(AssignmentDto dto, DateTime nowUtc)
    {
        var end = dto.CompletedAt is { } completed ? AsUtc(completed) : nowUtc;
        var elapsed = end - AsUtc(dto.CreatedAt);
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private void StopPolling()
    {
        _isPolling = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
