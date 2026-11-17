using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>One tool call under its step. Argument and result texts are user content: bound, never logged.</summary>
public sealed partial class RunStepToolRowViewModel : ObservableObject
{
    internal const int ArgumentLineMax = 60;

    private readonly ILocalizationService _localization;
    private readonly Func<CancellationToken, Task<AgentStepCallDetails>>? _loadStepCalls;

    public RunStepToolRowViewModel(Guid eventId, ILocalizationService localization,
        Func<CancellationToken, Task<AgentStepCallDetails>>? loadStepCalls)
    {
        EventId = eventId;
        _localization = localization;
        _loadStepCalls = loadStepCalls;
    }

    public Guid EventId { get; }

    public string ToolName { get; set; } = string.Empty;

    public string? ToolCallId { get; set; }

    public int? Round { get; set; }

    /// <summary>Position among the step's calls with the same name and round, for matching a call without an id.</summary>
    public int Occurrence { get; set; }

    [ObservableProperty]
    private string? _durationLabel;

    [ObservableProperty]
    private string? _outcomeLabel;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArgumentLine))]
    private string? _argumentLine;

    public bool HasArgumentLine => !string.IsNullOrEmpty(ArgumentLine);

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInput))]
    private string? _inputText;

    public bool HasInput => !string.IsNullOrEmpty(InputText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputNote))]
    private string? _inputNote;

    public bool HasInputNote => !string.IsNullOrEmpty(InputNote);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultNote))]
    private string? _resultNote;

    public bool HasResultNote => !string.IsNullOrEmpty(ResultNote);

    /// <summary>Set when the call as a whole has no detail: not recorded, not found, or unreadable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailNote))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detailNote;

    public bool HasDetailNote => !string.IsNullOrEmpty(DetailNote);

    public bool HasDetail => !HasDetailNote;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded)
            await LoadDetailAsync(CancellationToken.None);
    }

    internal async Task LoadDetailAsync(CancellationToken ct)
    {
        if (_loadStepCalls is null)
        {
            ApplyDetail(null, AgentStepCallDetails.Empty, readFailed: false);
            return;
        }

        IsLoading = true;
        try
        {
            var details = await _loadStepCalls(ct);
            ApplyDetail(Match(details, ToolCallId, ToolName, Round, Occurrence), details, readFailed: false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            ApplyDetail(null, AgentStepCallDetails.Empty, readFailed: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void ApplyDetail(AgentToolCallDetail? call, AgentStepCallDetails details, bool readFailed)
    {
        InputText = ResultText = InputNote = ResultNote = DetailNote = null;

        if (readFailed)
        {
            DetailNote = _localization["RunStepDetail_ReadFailed"];
            return;
        }

        if (call is null)
        {
            DetailNote = Round is { } r && details.NotRecordedRounds.Contains(r)
                ? _localization["RunStepDetail_NotRecordedRound"]
                : _localization["RunStepDetail_NoRecord"];
            return;
        }

        ArgumentLine = ShortArgumentLine(call.Arguments);
        InputText = call.Arguments;
        InputNote = StateNote(call.ArgumentsState) ?? (string.IsNullOrEmpty(call.Arguments)
            ? _localization["RunStepDetail_NoInput"]
            : null);
        ResultText = call.Result;
        ResultNote = StateNote(call.ResultState);
    }

    private string? StateNote(AgentToolDetailState state) => state switch
    {
        AgentToolDetailState.Truncated => _localization["RunStepDetail_State_Truncated"],
        AgentToolDetailState.Omitted => _localization["RunStepDetail_State_Omitted"],
        AgentToolDetailState.Missing => _localization["RunStepDetail_State_Missing"],
        _ => null,
    };

    /// <summary>A unique call id wins; otherwise the same name and round, taken in call order.</summary>
    internal static AgentToolCallDetail? Match(AgentStepCallDetails details, string? callId, string toolName,
        int? round, int occurrence)
    {
        if (!string.IsNullOrEmpty(callId))
        {
            var byId = details.Calls.Where(c => c.CallId == callId).ToList();
            if (byId.Count == 1) return byId[0];
        }

        var candidates = details.Calls
            .Where(c => string.Equals(c.ToolName, toolName, StringComparison.Ordinal)
                        && (round is null || c.Round is null || c.Round == round))
            .ToList();
        return occurrence >= 0 && occurrence < candidates.Count ? candidates[occurrence] : null;
    }

    private static readonly string[] PreferredArgumentKeys = ["query", "url", "path", "file", "name", "title", "command"];

    internal static string? ShortArgumentLine(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        string text;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            text = doc.RootElement.ValueKind == JsonValueKind.Object
                ? PickArgument(doc.RootElement) ?? doc.RootElement.GetRawText()
                : doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            text = argumentsJson;
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= ArgumentLineMax ? text : text[..(ArgumentLineMax - 1)] + "…";
    }

    private static string? PickArgument(JsonElement obj)
    {
        var strings = obj.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
            .ToList();
        if (strings.Count == 0) return null;

        var preferred = strings.FirstOrDefault(p =>
            PreferredArgumentKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        var value = (preferred.Value.ValueKind == JsonValueKind.String ? preferred : strings[0]).Value.GetString()!;
        return $"\"{value}\"";
    }

    internal static string FormatDuration(long durationMs) =>
        (durationMs / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " s";
}
