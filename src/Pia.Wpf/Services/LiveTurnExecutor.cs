using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

namespace Pia.Services;

/// <summary>
/// Interactive act-step executor bound to a live <see cref="ChatSession"/>. Constructed on the UI
/// thread by <c>ChatSessionManager</c>; captures the UI <see cref="SynchronizationContext"/> at
/// construction and <c>Post</c>s each step onto it, awaiting the result off-thread (§13.1/§B.4).
/// Streams into the transcript via the session's existing machinery (action cards + gate unchanged).
/// The orchestrator drives it thread-agnostically.
/// </summary>
public sealed class LiveTurnExecutor : IAgentTurnExecutor
{
    private readonly ChatSession _session;
    private readonly SynchronizationContext _ui;
    private readonly Func<ChatSession, bool> _isActive;
    private readonly PersonaAttribution _persona;
    private readonly AiProvider _provider;
    private readonly AssistantTurnSetup _turnSetup;
    private readonly bool _tokenizationEnabled;

    public LiveTurnExecutor(
        ChatSession session,
        Func<ChatSession, bool> isActive,
        PersonaAttribution persona,
        AiProvider provider,
        AssistantTurnSetup turnSetup,
        bool tokenizationEnabled)
    {
        _session = session;
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("LiveTurnExecutor must be constructed on the UI thread.");
        _isActive = isActive;
        _persona = persona;
        _provider = provider;
        _turnSetup = turnSetup;
        _tokenizationEnabled = tokenizationEnabled;
    }

    public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        PostAsync(() =>
        {
            // The manager pre-added an empty streaming placeholder AssistantMessage; remove it so the
            // transcript starts as [user: goal] and each step adds its own persona-attributed reply.
            var placeholder = _session.Messages.LastOrDefault(m => !m.IsUser && m.IsStreaming);
            if (placeholder is not null)
                _session.Messages.Remove(placeholder);
            // The session is already Running (the manager flipped it before dispatch); stays Running
            // across all steps (§16 R12).
            return Task.CompletedTask;
        });

    public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct) =>
        PostAsync(() => _session.RunStepTurnAsync(BuildSpec(run, step.Ordinal, step.Intent ?? string.Empty,
            step.ExpectedArtifact, useGoalVerbatim: false), ctx, ct));

    public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        PostAsync(() => _session.RunStepTurnAsync(BuildSpec(run, 0, ctx.Goal, null, useGoalVerbatim: true), ctx, ct));

    public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct) =>
        PostAsync(() =>
        {
            // Per-run terminal finalize mirror (§13.5 step 2 / §16 R4): dispose the session CTS, settle
            // terminal state, raise TurnCompleted — the equivalents RunTurnAsync runs inline per turn.
            _session.DisposeCts();

            // A cancelled OR failed run never counts as producing content: a Failed step's catch handler
            // writes error text (e.g. "Error: boom") into its assistant message, so keying purely off the
            // last message's Content would settle a Failed run as Completed/Succeeded (§13.5.2/§16 R4).
            var lastAssistant = _session.Messages.LastOrDefault(m => !m.IsUser);
            var producedContent = !cancelled && !failed && !string.IsNullOrEmpty(lastAssistant?.Content);
            if (_session.State != ChatState.Error)
            {
                _session.SetState(producedContent && !_isActive(_session)
                    ? ChatState.Completed
                    : ChatState.Idle);
            }

            _session.RaiseTurnCompleted(new TurnCompletedEventArgs { Succeeded = producedContent });
            return Task.CompletedTask;
        });

    private StepTurnSpec BuildSpec(AgentRun run, int ordinal, string intent, string? expectedArtifact, bool useGoalVerbatim) =>
        new(
            RunId: run.Id,
            Ordinal: ordinal,
            Intent: intent,
            ExpectedArtifact: expectedArtifact,
            SystemPrompt: _turnSetup.SystemPrompt,
            Persona: _persona,
            Provider: _provider,
            Tools: _turnSetup.Tools,
            SupportsTools: _turnSetup.SupportsTools,
            WebSearchActive: _turnSetup.WebSearchActive,
            TokenizationEnabled: _tokenizationEnabled,
            UseGoalVerbatim: useGoalVerbatim);

    /// <summary>Marshals <paramref name="work"/> onto the captured UI context and bridges it back to an awaitable.</summary>
    private Task PostAsync(Func<Task> work) => PostAsync(async () => { await work(); return true; });

    private Task<T> PostAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try { tcs.SetResult(await work()); }
            catch (OperationCanceledException) { tcs.SetCanceled(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
}
