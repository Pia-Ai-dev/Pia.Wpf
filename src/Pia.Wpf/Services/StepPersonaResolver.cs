using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Resolves the <see cref="StepPersonaSetup"/> one act step of a <see cref="RunShape.Planned"/> run runs on
/// (Batch 07 D3/D5/D6), and produces the roster the planner is allowed to assign from (D1/D7).
/// <para>
/// <b>One instance per run</b> — registered <c>AddTransient</c>, and both executors plus the planner take
/// their own. That lifetime is load-bearing, not incidental: this type memoizes the composed
/// <see cref="AssistantTurnSetup"/> per persona id, so a singleton would pin a STALE system prompt across a
/// persona edit or a roster change until the app restarted, silently. Per-run is also the semantics we want
/// in the other direction: a persona edited halfway through a 24-step run must not change the prompt at
/// step 13.
/// </para>
/// <para>
/// <b>Nothing in here may fail a step.</b> A per-step persona is an ENHANCEMENT over "the whole run uses the
/// run persona"; every arm that cannot produce one returns the run default instead of throwing. That is why
/// there is no <c>IStepPersonaResolver</c> either — there is exactly one behaviour worth having and both
/// executor test suites construct this type directly.
/// </para>
/// </summary>
public sealed class StepPersonaResolver
{
    private readonly IPersonaService _personas;
    private readonly IProviderService _providers;
    private readonly IAssistantPromptComposer _composer;
    private readonly ISettingsService _settings;
    private readonly ILogger<StepPersonaResolver> _logger;

    /// <summary>
    /// Memo of the resolved setup per persona id, for the life of this instance (= one run). Recomposing the
    /// system prompt on all 24 steps of a run is pure waste, and the composition is the expensive half.
    /// <para>
    /// Deliberately a plain <see cref="Dictionary{TKey,TValue}"/> and deliberately NOT thread-safe: one
    /// resolver belongs to one executor and an executor's steps are strictly sequential (the orchestrator
    /// awaits each <c>ExecuteStepAsync</c> before the next). A <c>ConcurrentDictionary</c> here would imply a
    /// concurrency that does not exist and invite someone to rely on it.
    /// </para>
    /// </summary>
    private readonly Dictionary<Guid, StepPersonaSetup> _memo = new();

    /// <summary>
    /// Ids already known to degrade to the run default. Without it, a plan that assigns all 24 steps to a
    /// since-deleted persona would repeat the lookup AND the log line 24 times; the answer cannot change
    /// within one run, because the roster is resolved once (below) and the persona store is not re-read.
    /// </summary>
    private readonly HashSet<Guid> _degraded = new();

    /// <summary>The roster, resolved at most once per instance (see <see cref="GetRosterAsync"/>).</summary>
    private IReadOnlyList<Persona>? _roster;

    public StepPersonaResolver(
        IPersonaService personas,
        IProviderService providers,
        IAssistantPromptComposer composer,
        ISettingsService settings,
        ILogger<StepPersonaResolver> logger)
    {
        _personas = personas;
        _providers = providers;
        _composer = composer;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// The (persona, provider, turn setup) this step runs on. The fallback ladder, in order, never throwing
    /// (D3):
    /// <list type="number">
    /// <item><paramref name="assignedPersonaId"/> is null ⇒ <paramref name="runDefault"/>. Today's behaviour
    /// and the overwhelmingly common case; nothing is even looked up.</item>
    /// <item>the id is not in the CURRENT roster ⇒ <paramref name="runDefault"/>. A plan outlives the setting
    /// that produced it (a replan, a resume, a roster the user has since edited), and a model naming a
    /// persona the user did not put on the roster must not get it.</item>
    /// <item>the id resolves to nothing (the persona was deleted between plan and execute) ⇒
    /// <paramref name="runDefault"/>.</item>
    /// <item>the persona resolves but no provider does ⇒ <b>keep the assigned persona and its turn setup and
    /// borrow the RUN's provider.</b> This is the one PARTIAL arm, and it is partial on purpose: discarding
    /// the persona would throw away its SYSTEM PROMPT, which is the entire substance of assigning the step.
    /// A persona on a borrowed provider is still that persona; the run persona wearing the step's label is
    /// not.</item>
    /// <item><c>PrepareTurn</c> throws ⇒ <paramref name="runDefault"/>. Defence in depth: it composes
    /// strings, and this arm is what stops one bad persona prompt from failing every step of a run.</item>
    /// </list>
    /// </summary>
    public async Task<StepPersonaSetup> ResolveAsync(
        Guid? assignedPersonaId, StepPersonaSetup runDefault, bool tokenizationEnabled, CancellationToken ct)
    {
        if (assignedPersonaId is not { } id || id == Guid.Empty)
            return runDefault;

        if (_memo.TryGetValue(id, out var cached))
            return cached;
        if (_degraded.Contains(id))
            return runDefault;

        // Roster containment IS the persona lookup: an id off the roster is not this run's business even if it
        // resolves, and GetRosterAsync has already fetched the full Persona objects (same column list, same
        // MapPersona) and dropped every configured id that no longer resolves — logging that count itself. So
        // the roster entry is the persona, and a second `GetPersonaAsync(id)` round-trip would only re-read
        // what is already in hand.
        //
        // It would also be the ONE ARM OF THIS LADDER THAT CAN THROW, which this type's contract forbids:
        // PersonaService.GetPersonaAsync executes raw SQLite I/O, and step-persona resolution happens BEFORE
        // each executor's exchange try/catch — so a momentarily busy connection (the sync or vault writer
        // holding the DB) escaped all the way to the orchestrator's outer catch and FAILED THE WHOLE RUN,
        // losing the completed steps' progress on a run that would have finished with no assignment at all.
        //
        // An empty roster (the D1 default) therefore assigns nothing, which is the property that makes the
        // whole batch off-by-default at this seam too, not only at the planner's.
        var roster = await GetRosterAsync(ct).ConfigureAwait(false);
        var persona = roster.FirstOrDefault(p => p.Id == id);
        if (persona is null)
        {
            // Ids, counts and reason tokens only — a persona NAME is user-named content (CLAUDE.md).
            _logger.LogInformation(
                "Step persona {PersonaId} is not on the current roster ({RosterCount} persona(s)); using the run persona ({Reason})",
                id, roster.Count, "off-roster");
            _degraded.Add(id);
            return runDefault;
        }

        var provider = await ResolveProviderAsync(persona, runDefault, ct).ConfigureAwait(false);

        AssistantTurnSetup setup;
        try
        {
            // The exact argument shape both run-turn call sites use. suggestAgentModeEligible is false and
            // must stay false: suggest_agent_mode offers to switch the USER into Agent mode, and there is no
            // user inside a run to offer it to.
            setup = _composer.PrepareTurn(persona, provider, [], tokenizationEnabled, suggestAgentModeEligible: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Composing the turn setup for step persona {PersonaId} failed; using the run persona ({Reason})",
                id, "prepare-failed");
            _degraded.Add(id);
            return runDefault;
        }

        var resolved = new StepPersonaSetup(persona, provider, setup);
        _memo[id] = resolved;
        return resolved;
    }

    /// <summary>
    /// The provider ladder for an assigned persona (D5): its <see cref="Persona.PreferredProviderId"/>, then
    /// the Assistant-mode default, then — as the last arm — the RUN's provider.
    /// <para>
    /// The launcher's provider OVERRIDE deliberately does NOT win here. It exists so a run's executor and its
    /// planner share one provider (honouring a scheduled job's ProviderId), but a roster persona was chosen
    /// BECAUSE of its provider/effort, and an override that won everywhere would make the roster's provider
    /// column decorative on every scheduled job. It still wins on the fallback arm, since the run default IS
    /// the override — which is also the mitigation for the case where an explicit ProviderId exists precisely
    /// because the mode default is unusable.
    /// </para>
    /// </summary>
    private async Task<AiProvider> ResolveProviderAsync(
        Persona persona, StepPersonaSetup runDefault, CancellationToken ct)
    {
        AiProvider? provider = null;
        try
        {
            if (persona.PreferredProviderId.HasValue)
                provider = await _providers.GetProviderAsync(persona.PreferredProviderId.Value).ConfigureAwait(false);
            provider ??= await _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Resolving a provider for step persona {PersonaId} failed; borrowing the run's provider ({Reason})",
                persona.Id, "no-provider");
        }

        if (provider is null)
        {
            _logger.LogInformation(
                "Step persona {PersonaId} has no resolvable provider; it runs on the run's provider ({Reason})",
                persona.Id, "no-provider");
            return runDefault.Provider;
        }

        if (persona.ReasoningEffort.HasValue)
        {
            // CLONE, never mutate: AiProvider instances come out of a shared store, so setting the effort on
            // the instance we were handed would leak one persona's effort into every other consumer of that
            // provider in the process. Same two lines the run-level resolution already uses.
            provider = provider.Clone();
            provider.ReasoningEffort = persona.ReasoningEffort.Value;
        }
        return provider;
    }

    /// <summary>
    /// The personas a plan may assign steps to, for the CURRENT operating mode: the configured roster,
    /// deduped and clamped by <see cref="AppSettings.GetAgentPersonaRoster"/>, with ids that no longer
    /// resolve to a persona dropped (a deleted persona must not reach a prompt as a blank line).
    /// <para>
    /// <b>EMPTY when nothing is configured, which is the whole opt-in (D1):</b> an empty roster means the
    /// plan prompt is byte-identical to the pre-Phase-3 one and no step is ever assigned.
    /// </para>
    /// <para>
    /// Resolved at most ONCE per instance (= per run) and then reused, so a plan and its replans list the same
    /// specialists and the executor's containment check agrees with what the planner was shown. Any failure
    /// answers "empty roster", i.e. today.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Persona>> GetRosterAsync(CancellationToken ct)
    {
        if (_roster is not null)
            return _roster;

        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            var ids = settings.GetAgentPersonaRoster(settings.UserOperatingMode ?? UserOperatingMode.Personal);
            if (ids.Count == 0)
                return _roster = [];

            var all = await _personas.GetPersonasAsync().ConfigureAwait(false);
            var byId = all.ToDictionary(p => p.Id);
            var roster = new List<Persona>(ids.Count);
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var persona))
                    roster.Add(persona);
            }
            if (roster.Count != ids.Count)
            {
                _logger.LogInformation(
                    "Agent persona roster dropped {DroppedCount} configured id(s) that no longer resolve to a persona",
                    ids.Count - roster.Count);
            }
            return _roster = roster;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reading the roster is the GATE of an optional feature and must never be able to fail a run:
            // GetSettingsAsync does I/O and the persona store can fault. Either way the answer is "no
            // specialists", which is exactly today's behaviour. Exception TYPE only — a persona store's
            // message can embed a persona name.
            _logger.LogWarning("Agent persona roster could not be read ({Error}); no step specialists this run.",
                ex.GetType().Name);
            return _roster = [];
        }
    }
}
