using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed partial class RunProgressViewModel
{
    internal const int LiveTailChars = 400;

    // Bounded so a long step cannot grow the buffer without limit; the row shows the tail only.
    private const int LiveBufferChars = 8000;

    private Action? _detachLiveText;
    private readonly object _liveGate = new();
    private readonly Dictionary<Guid, (StringBuilder Text, StringBuilder Reasoning)> _liveBuffers = [];
    private bool _liveFlushPending;

    internal void AttachLiveText(IRunLiveTextWatcher? carrier)
    {
        if (carrier is null || _detachLiveText is not null) return;
        carrier.TextAppended += OnLiveText;
        _detachLiveText = () => carrier.TextAppended -= OnLiveText;
    }

    private void DetachStepDetail()
    {
        _detachLiveText?.Invoke();
    }

    private void OnLiveText(RunLiveTextChunk chunk)
    {
        if (chunk.RunId != _runId || chunk.StepId is not { } stepId) return;

        lock (_liveGate)
        {
            switch (chunk.Kind)
            {
                case RunLiveTextKind.Ended:
                    _liveBuffers[stepId] = (new StringBuilder(), new StringBuilder());
                    break;
                case RunLiveTextKind.RoundBreak:
                    if (_liveBuffers.TryGetValue(stepId, out var round))
                    {
                        round.Text.Clear();
                        round.Reasoning.Clear();
                    }
                    break;
                default:
                    if (!_liveBuffers.TryGetValue(stepId, out var buffers))
                        _liveBuffers[stepId] = buffers = (new StringBuilder(), new StringBuilder());
                    var target = chunk.Kind == RunLiveTextKind.Reasoning ? buffers.Reasoning : buffers.Text;
                    target.Append(chunk.Text);
                    if (target.Length > LiveBufferChars)
                        target.Remove(0, target.Length - LiveBufferChars);
                    break;
            }

            if (_liveFlushPending) return;
            _liveFlushPending = true;
        }

        _uiContext.Post(_ => FlushLiveText(), null);
    }

    internal void FlushLiveText()
    {
        if (_disposed) return;
        Dictionary<Guid, (string Text, string Reasoning)> snapshot;
        lock (_liveGate)
        {
            _liveFlushPending = false;
            snapshot = _liveBuffers.ToDictionary(kv => kv.Key, kv => (kv.Value.Text.ToString(), kv.Value.Reasoning.ToString()));
            foreach (var key in _liveBuffers.Where(kv => kv.Value.Text.Length == 0 && kv.Value.Reasoning.Length == 0)
                         .Select(kv => kv.Key).ToList())
                _liveBuffers.Remove(key);
        }

        foreach (var row in Steps)
        {
            if (!snapshot.TryGetValue(row.StepId, out var live)) continue;
            if (!row.IsRunning)
            {
                row.ClearLive();
                continue;
            }

            var (visible, thinking) = StreamThinkTagParser.Parse(live.Text);
            row.LiveText = Tail(visible);
            row.LiveReasoning = Tail(live.Reasoning + thinking);
        }
    }

    internal static string? Tail(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        return text.Length <= LiveTailChars ? text : "…" + text[^LiveTailChars..].TrimStart();
    }

    private void ApplyStepToolRows()
    {
        var calls = VisibleTimelineEvents()
            .Where(e => e.Kind == AgentTimelineEventKind.ToolCall && e.StepId is not null)
            .Reverse()
            .ToList();

        foreach (var step in Steps)
        {
            var wanted = calls.Where(e => e.StepId == step.StepId).ToList();
            var wantedIds = wanted.Select(e => e.Id).ToHashSet();
            for (var i = step.ToolRows.Count - 1; i >= 0; i--)
            {
                if (!wantedIds.Contains(step.ToolRows[i].EventId))
                    step.ToolRows.RemoveAt(i);
            }

            var added = false;
            var occurrences = new Dictionary<(string, int?), int>();
            for (var i = 0; i < wanted.Count; i++)
            {
                var e = wanted[i];
                var key = (e.ToolName, e.Round);
                occurrences[key] = occurrences.TryGetValue(key, out var n) ? n + 1 : 0;

                var row = step.ToolRows.FirstOrDefault(r => r.EventId == e.Id);
                if (row is null)
                {
                    row = new RunStepToolRowViewModel(e.Id, _localization, StepCallsLoader(step.StepId));
                    step.ToolRows.Insert(Math.Min(i, step.ToolRows.Count), row);
                    added = true;
                }

                row.ToolName = e.ToolName;
                row.ToolCallId = e.ToolCallId;
                row.Round = e.Round;
                row.Occurrence = occurrences[key];
                row.DurationLabel = e.DurationMs is { } ms ? RunStepToolRowViewModel.FormatDuration(ms) : null;
                row.IsFailed = e.Outcome == AgentTimelineOutcome.Error;
                row.OutcomeLabel = e.Outcome switch
                {
                    AgentTimelineOutcome.Ok => _localization["RunStepDetail_Outcome_Ok"],
                    AgentTimelineOutcome.Error => _localization["Run_Timeline_Outcome_Failed"],
                    _ => _localization[RowLabelKey(e)],
                };
            }

            if (added || !step.IsRunning)
                FillArgumentLines(step);
        }
    }

    private readonly HashSet<(Guid StepId, int Rows, bool Running)> _argumentLineReads = [];

    private void FillArgumentLines(StepRowViewModel step)
    {
        if (StepCallsLoader(step.StepId) is not { } load || step.ToolRows.All(r => r.HasArgumentLine)
            || !_argumentLineReads.Add((step.StepId, step.ToolRows.Count, step.IsRunning)))
            return;

        var rows = step.ToolRows.ToList();
        FillArgumentLinesAsync(load, rows).SafeFireAndForget(_logger);
    }

    private async Task FillArgumentLinesAsync(
        Func<CancellationToken, Task<AgentStepCallDetails>> load, IReadOnlyList<RunStepToolRowViewModel> rows)
    {
        AgentStepCallDetails details;
        try { details = await load(CancellationToken.None); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId}: reading a step's tool arguments failed", _runId);
            return;
        }
        if (details is null) return;

        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            foreach (var row in rows)
                row.ApplyArgumentLine(details);
        }, null);
    }

    private Func<CancellationToken, Task<AgentStepCallDetails>>? StepCallsLoader(Guid stepId) =>
        _toolCalls is null
            ? null
            : ct => Task.Run(() => _toolCalls.GetStepCallsAsync(_runId, stepId, ct), ct);
}
