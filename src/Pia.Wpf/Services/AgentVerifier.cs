using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Terminal critic (§13.x). Judges goal-achievement via a single constrained <c>emit_verdict</c>
/// turn, draining the whole stream (R6-style). On no-call it retries once (firmer); on still-no-call
/// it degrades to ACCEPT rather than wedging an otherwise-successful run. Sums provider usage from
/// the yielded <see cref="Finished"/> items so the orchestrator can accrue it run-level. Verdict
/// text (reason/missing) is SENSITIVE — logged only via <see cref="LoggingExtensions.SensitiveDebug"/>.
/// <para>
/// H1: the verdict is anchored in MECHANICAL evidence, not only the model's self-summaries — each
/// completed step's declared <c>ExpectedArtifact</c> and its own reported artifact are probed against the
/// run's effective file root and the found/not-found facts are fed to the critic as a distinct block.
/// Two found artifacts of the same type, size band and name shape are flagged as a hint. The probe is bounded and
/// failure-isolated (any fault/timeout omits the block and verify proceeds) and it can never itself
/// fail a verdict: the LLM still renders it.
/// </para>
/// </summary>
public sealed class AgentVerifier : IAgentVerifier
{
    private readonly IAiClientService _ai;
    private readonly ISettingsService _settings;
    private readonly ILogger<AgentVerifier> _logger;
    private static readonly JsonSerializerOptions VerdictJson = new(JsonSerializerDefaults.Web);

    private static readonly AITool EmitVerdictTool = AIFunctionFactory.Create(
        EmitVerdictSchema, "emit_verdict",
        "Emit the verdict on whether the completed run achieved its goal.");

    public AgentVerifier(IAiClientService ai, ISettingsService settings, ILogger<AgentVerifier> logger)
    {
        _ai = ai;
        _settings = settings;
        _logger = logger;
    }

    [Description("Emit the verdict on whether the completed run achieved its goal.")]
    private static string EmitVerdictSchema(
        [Description("True only if the run genuinely achieved the goal and produced the expected artifacts.")]
        bool passed,
        [Description("A short justification for the verdict.")]
        string reason,
        [Description("The concrete goals or artifacts still missing when passed is false; empty when passed is true.")]
        string[] missing) => "";

    private sealed record EmitVerdictArgs(bool Passed, string? Reason, string[]? Missing);

    public async Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        // Probe ONCE for both attempts (H1): the facts cannot change between them, and re-probing would
        // double the bounded filesystem work. Null = no block (nothing declared, no root, or a fault).
        var artifactFacts = await TryBuildArtifactFactsAsync(ctx, ct).ConfigureAwait(false);

        var (args, usage) = await TryCaptureAsync(BuildVerifyMessages(ctx, persona, firm: false, artifactFacts), provider, ct).ConfigureAwait(false);
        if (args is null)
        {
            var (args2, usage2) = await TryCaptureAsync(BuildVerifyMessages(ctx, persona, firm: true, artifactFacts), provider, ct).ConfigureAwait(false); // retry once
            args = args2;
            usage = AgentTurnUsage.Sum(usage, usage2);
        }

        if (args is null)
        {
            _logger.LogInformation("Verifier degrade → accept (no valid emit_verdict).");
            return VerdictResult.Accept with { Usage = usage }; // still accrue the tokens spent
        }

        var reason = args.Reason?.Trim() ?? string.Empty;
        var missing = (args.Missing ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray();

        _logger.SensitiveDebug("Verifier verdict passed={Passed} reason={Reason} missing={Missing}",
            args.Passed, reason, string.Join(" | ", missing));

        return new VerdictResult(args.Passed, reason, missing, usage);
    }

    /// <summary>
    /// Runs one verify turn, capturing the final <c>emit_verdict</c> args (last-write-wins) while
    /// draining the whole stream; sums <see cref="Finished.Usage"/> across the drained items via the
    /// shared <see cref="AgentTurnUsage"/> (the planner does the same since I1, off one helper so the
    /// two accruals cannot drift). Returns (null, usage) when no verdict emitted.
    /// </summary>
    private async Task<(EmitVerdictArgs? Args, UsageDetails? Usage)> TryCaptureAsync(
        List<ChatMessage> messages, AiProvider provider, CancellationToken ct)
    {
        EmitVerdictArgs? captured = null;
        ToolCallHandler toolHandler = (call, _) =>
        {
            if (string.Equals(call.Name, "emit_verdict", StringComparison.Ordinal))
            {
                try
                {
                    var json = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
                    captured = JsonSerializer.Deserialize<EmitVerdictArgs>(json, VerdictJson); // last-write-wins
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse emit_verdict arguments");
                }
                return Task.FromResult<object?>("Verdict received.");
            }
            return Task.FromResult<object?>("Only emit_verdict is available here.");
        };

        UsageDetails? usage = null;
        await foreach (var item in _ai.GetChatCompletionWithToolsAsync(
            messages, provider, [EmitVerdictTool], toolHandler, mode: null, cancellationToken: ct).ConfigureAwait(false))
        {
            if (item is Finished { Usage: { } u })
                usage = AgentTurnUsage.Sum(usage, u); // capture usage from the yielded stream
        }

        return (captured, usage);
    }

    private static List<ChatMessage> BuildVerifyMessages(RunContext ctx, Persona persona, bool firm, string? artifactFacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("The run below has finished executing its plan. Judge whether it actually achieved the user's goal and produced the expected artifacts.");
        sb.AppendLine("Call the emit_verdict tool exactly once: passed=true ONLY if the goal is genuinely satisfied; otherwise passed=false with a short reason and the concrete missing items.");
        if (artifactFacts is not null)
        {
            sb.AppendLine();
            sb.AppendLine(ArtifactBlockHeader);
            sb.Append(artifactFacts); // already newline-terminated per line
            sb.AppendLine("A declared artifact reported NOT FOUND is a verify-relevant FACT: the step promised it and it is not on disk. Weigh it against the step results above — it is NOT an automatic failure (a step may legitimately declare a non-file outcome, and \"not a file reference\" carries no signal either way).");
        }
        if (firm)
            sb.AppendLine("You did not call emit_verdict. You MUST respond by calling the emit_verdict tool now — do not write prose.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            // Batch 08 D4: the critic seeing an active nudge is intentional (§1 D4 item 5) — never the System
            // prompt above, which is model/persona text, not the user's. Batch 08 F11 moves the executed-step
            // listing here for exactly the same reason; see BuildExecutedSteps.
            new(ChatRole.User, ctx.AppendNudge(ctx.Goal + BuildExecutedSteps(ctx))),
        };
    }

    /// <summary>
    /// The "Steps executed" listing, as a USER-message block.
    /// <para>
    /// <b>Batch 08 F11: this used to be appended to the System prompt, and that is a PII leak.</b>
    /// <c>TokenizingAiClientService.TokenizeMessages</c> short-circuits on <c>msg.Role != ChatRole.User</c>, so
    /// a System message ships verbatim even while tokenization is ON. Every title and intent here comes from
    /// <c>ctx.RecordStep</c>, i.e. from the PERSISTED step row — and since Batch 08 D3 that row can hold raw
    /// user keystrokes typed into the run panel's inline editor. So the same edited text was tokenized when it
    /// rode the step instruction (a User message) and untokenized when it rode the verify prompt of the same
    /// run: pause a run, edit a step's intent to "Mail the signed contract to john.doe@acme.com", continue,
    /// and the step turn ships <c>[Email_1]</c> while the verify turn ships the address.
    /// </para>
    /// <para>
    /// Moved rather than tokenized in place: the token map lives BELOW this class (it is the AI-client
    /// wrapper's, keyed to the running turn), so composing against it here would mean reaching for ambient
    /// state and re-deciding whether tokenization is on. The precedent is already written twice in this
    /// codebase — the nudge (§1 D4 item 7) and <c>AgentPlanner</c>'s reasoning analysis — both of which ride
    /// the User message for this exact reason and say so at the call site.
    /// </para>
    /// <para>
    /// Returns <c>""</c> when nothing has executed, so the goal-only shape is byte-identical to before.
    /// </para>
    /// </summary>
    private static string BuildExecutedSteps(RunContext ctx)
    {
        if (ctx.CompletedSteps.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Steps executed (with their results, as reported by the assistant itself):");
        // hermes #9. The tag used to be "ok"/"failed" and both meanings were a guess: "ok" only ever meant
        // "the step emitted some text". Now it says HOW the verdict was reached, because the critic's whole
        // job is to catch a run that believes its own false premise — and "the step never said whether it
        // worked" is the single most useful thing it can know about a step.
        sb.AppendLine(
            "Tags: [declared] = the step called emit_step_result and this is its own structured verdict; "
            + "[unconfirmed] = it never declared an outcome, so \"ok\" only means it produced some text and "
            + "may be wrong; [observed] = the run itself saw the step fail (error or empty reply).");
        foreach (var c in ctx.CompletedSteps)
        {
            // Title/Intent are flattened for the same reason as in the facts block: a planner title is
            // model text, and a newline in it would otherwise let a step's own label imitate a
            // "- step N … → found" fact line. The result text below is deliberately NOT flattened —
            // it is prose the prompt explicitly frames as the assistant's self-report, and the facts
            // block is what anchors the verdict.
            sb.AppendLine($"- [{OutcomeTag(c)}] {Flatten(c.Title)}: {Flatten(c.Intent)}");
            // The artifact the step says it PRODUCED, as opposed to the one the planner DECLARED (which the
            // facts block below probes). Already flattened and capped at parse time.
            if (!string.IsNullOrWhiteSpace(c.Outcome?.ArtifactRef))
                sb.AppendLine($"    produced: {c.Outcome.ArtifactRef}");
            if (!string.IsNullOrWhiteSpace(c.VisibleText))
                sb.AppendLine($"    result: {c.VisibleText}");
            else if (c.FromEarlierSegment) // E2: a resumed run's pre-pause steps carry no result text
                sb.AppendLine($"    result: {CompletedStepSummary.EarlierSegmentNote}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// hermes #9: how a step's ok/failed was ESTABLISHED, not just what it was.
    /// <para>
    /// A step with a claim declared its own outcome. A step without one that "succeeded" did so only by the
    /// legacy non-empty-text heuristic — that is <c>unconfirmed</c>, and it is the case the critic must be
    /// suspicious of. A step without a claim that failed was failed by the run itself (exception, timeout,
    /// empty reply), which is machine-observed and needs no hedging.
    /// </para>
    /// </summary>
    internal static string OutcomeTag(CompletedStepSummary c) => c.Outcome switch
    {
        { Succeeded: true } => "ok, declared",
        { Succeeded: false } => "failed, declared",
        _ => c.Succeeded ? "ok, unconfirmed" : "failed, observed",
    };

    // ---- H1: declared-artifact probe (mechanical evidence for the verdict) ----

    internal const string ArtifactBlockHeader =
        "Declared-artifact probe — mechanical filesystem facts gathered by the app, NOT the assistant's claims:";

    // Hard bounds so a long plan can never stall the verify turn (the probe is pure metadata: no file
    // contents are read). A 48-step plan therefore costs at most MaxProbedPaths stat calls.
    private const int MaxProbedPaths = 12;
    private const int MaxReportedSteps = 20;
    private const int MaxCandidatesPerDeclaration = 3;
    private const int MaxDuplicatePairs = 3;
    private const double MinSizeRatio = 0.5;
    private const int MinSharedTokenChars = 4;

    /// <summary>Cap for BOTH interpolated free-text fields of a fact line (declaration and step title).</summary>
    private const int MaxDeclarationChars = 200;
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(2);

    // Splits a free-text declaration into candidate tokens. ':' and '\\'/'/' are deliberately NOT
    // separators — a path keeps its separators, and splitting a drive letter off "C:\x" would mangle the
    // token before containment can reject it.
    private static readonly char[] TokenSeparators =
        [' ', '\t', '\r', '\n', ',', ';', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>', '|', '*', '?', '='];

    // Trailing prose punctuation only ("…to report.md." → "report.md"). Leading characters are left
    // alone: stripping a leading '.' would turn "./report.md" into the rooted "/report.md".
    private static readonly char[] TrailingTrim = ['.', ',', ';', ':', '!', '?', '"', '\'', '`', '*', '-'];

    /// <summary>
    /// Builds the artifact-fact block, or null when there is nothing to say (no declared artifacts, no
    /// usable file root) or the probe could not complete. Failure-isolated (guardrail 1): every fault
    /// omits the block and lets verify proceed — the ONLY exception is a genuine run cancel, which
    /// propagates (the orchestrator's SafeVerify must see it, not a degrade-to-accept).
    /// </summary>
    private async Task<string?> TryBuildArtifactFactsAsync(RunContext ctx, CancellationToken ct)
    {
        Task<(string? Facts, ArtifactProbeTally Tally, bool SubpathFallback)>? probe = null;
        try
        {
            var targets = ctx.CompletedSteps.Select(BuildTarget)
                .Where(t => t.Declared is not null || t.Reported is not null)
                .ToList();
            if (targets.Count == 0)
                return null; // neither channel named an artifact — no facts to add

            // Only the settings read happens here (a local DB read, not a filesystem walk). Everything that
            // TOUCHES the filesystem — including resolving the root — runs inside the time-boxed task below.
            // ctx FIRST: verify runs on the orchestrator thread where the per-step ambient is already
            // restored (Batch 06 B3). The ambient read is kept as the second choice for any caller that DOES
            // verify inside a step flow; the settings folder stays the last resort.
            var ambientRoot = ctx.WorkspaceRoot ?? TaskAmbient.Current?.WorkspaceRoot;
            var configured = ambientRoot ?? (await _settings.GetSettingsAsync().ConfigureAwait(false)).AssistantFilesFolder;
            if (string.IsNullOrWhiteSpace(configured))
            {
                _logger.LogInformation("Artifact probe skipped for {Count} step(s) with a declared or reported artifact: no usable files folder.", targets.Count);
                return null;
            }

            var workingSubpath = ctx.WorkingSubpath;

            // Off the caller's thread and time-boxed: the folder can be a slow or dead network share, and
            // a hung stat must never hold up the verify turn. The ROOT RESOLUTION is inside the box on
            // purpose — Directory.Exists/Canonicalize on a dead UNC path is exactly the call that blocks
            // (for the SMB connect timeout, tens of seconds), so resolving it before the box would leave
            // the advertised budget covering only the cheap part.
            probe = Task.Run<(string? Facts, ArtifactProbeTally Tally, bool SubpathFallback)>(() =>
            {
                var root = ResolveProbeRoot(configured, workingSubpath, out var fallback);
                if (root is null)
                    return (null, default, fallback);
                var result = ProbeDeclarations(root, targets);
                return (result.Facts, result.Tally, fallback);
            }, CancellationToken.None);
            var (facts, tally, subpathFallback) = await probe.WaitAsync(ProbeBudget, ct).ConfigureAwait(false);
            if (facts is null)
            {
                _logger.LogInformation("Artifact probe skipped for {Count} step(s) with a declared or reported artifact: files folder does not exist.", targets.Count);
                return null;
            }

            if (subpathFallback)
                _logger.SensitiveDebug("Working subpath did not resolve to an existing folder under the sandbox: {Subpath}", workingSubpath);

            _logger.LogInformation(
                "Artifact probe: declared={Declared} reported={Reported} reportedSame={ReportedSame} fileShaped={FileShaped} notFileShaped={NotFileShaped} overReportCap={OverReportCap} probed={Probed} found={Found} notFound={NotFound} folder={Folder} unresolvable={Unresolvable} uninspectable={Uninspectable} vaultRef={VaultRef} overPathCap={OverPathCap} dupPairs={DupPairs}",
                tally.Declared, tally.Reported, tally.ReportedSame, tally.FileShaped, tally.NotFileShaped,
                tally.OverReportCap, tally.Probed, tally.Found, tally.NotFound, tally.Folder, tally.Unresolvable,
                tally.Uninspectable, tally.VaultRef, tally.OverPathCap, tally.DupPairs);
            _logger.SensitiveDebug("Artifact probe facts:\n{Facts}", facts);
            return facts;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine run cancellation — never swallowed into a degrade
        }
        catch (Exception ex)
        {
            // Includes the TimeoutException from WaitAsync. Observe the abandoned probe's fault so a
            // slow/faulting stat cannot surface later as an unobserved task exception.
            if (probe is not null)
                _ = probe.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            _logger.LogWarning(ex, "Artifact probe failed — verifying without the artifact block.");
            return null;
        }
    }

    /// <summary>
    /// The run's effective file root, resolved and canonicalized exactly like
    /// <c>FilesToolHandler.HandleToolCallAsync</c> does: the base is the caller-supplied
    /// <paramref name="configured"/> value — the run's isolated workspace root
    /// (<c>RunContext.WorkspaceRoot</c>, preferred) or ambient workspace root when one is set, otherwise the
    /// configured assistant files folder — then <paramref name="workingSubpath"/> narrows it. This comment
    /// used to assert that <c>WorkspaceRoot</c> is null in production and the settings folder IS the root
    /// the step writes landed in; Batch 06 falsifies that once a run's steps write into an isolated
    /// workspace, which is exactly why the caller now resolves <c>ctx.WorkspaceRoot</c> first (B3).
    /// Null when no usable folder exists. Canonicalizing here means a junction in the root path itself is
    /// not a hole in the containment check below. Blocking — call it inside the probe's time box.
    /// </summary>
    private static string? ResolveProbeRoot(string configured, string? workingSubpath, out bool subpathFallback)
    {
        subpathFallback = false;
        var full = Path.GetFullPath(configured);
        if (!Directory.Exists(full))
            return null;
        var root = SafeFolderPath.Canonicalize(full);

        // Mirror of FilesToolHandler.ResolveEffectiveRoot (which GitToolHandler also duplicates): an
        // interactive chat scoped to a working subpath writes its files UNDER it, so probing the base root
        // would report every artifact the run actually delivered as NOT FOUND — a confident false fact,
        // which is worse than no fact. Same fail-safe direction as the file tools: a subpath that escapes
        // containment or does not exist falls back to the base root and never widens past it.
        if (!string.IsNullOrWhiteSpace(workingSubpath)
            && SafeFolderPath.TryResolveInsideAllowingAbsolute(root, workingSubpath, out var narrowed)
            && Directory.Exists(narrowed))
        {
            return narrowed;
        }

        subpathFallback = !string.IsNullOrWhiteSpace(workingSubpath);
        return root;
    }

    private enum ProbeOutcome { Found, NotFound, Folder, Unresolvable, Uninspectable, VaultReference }

    /// <summary>Counts only — unlike the fact lines, this is safe in a release log.</summary>
    private readonly record struct ArtifactProbeTally(
        int Declared, int Reported, int ReportedSame, int FileShaped, int NotFileShaped, int OverReportCap,
        int Probed, int Found, int NotFound, int Folder, int Unresolvable,
        int Uninspectable, int VaultRef, int OverPathCap, int DupPairs);

    /// <summary>One step's two artifact channels: what the planner declared, and what the step said it produced.</summary>
    private readonly record struct ProbeTarget(
        int Ordinal, string Title, string? Declared, string? Reported, bool ReportedSameAsDeclared);

    private readonly record struct FoundArtifact(int Ordinal, string Candidate, string Resolved, long Size);

    /// <summary>Mutable, so one probe body can serve both channels across two passes.</summary>
    private sealed class ProbeCounters
    {
        internal int Probed;
        internal int FileShaped;
        internal int NotFileShaped;
        internal int Found;
        internal int NotFound;
        internal int Folder;
        internal int Unresolvable;
        internal int Uninspectable;
        internal int VaultRef;
        internal int OverPathCap;
    }

    /// <summary>One step's targets. Every interpolated field is model text and is sanitized here: a newline in
    /// a title or a declaration could otherwise forge a second "- step N … → found" fact line.</summary>
    private static ProbeTarget BuildTarget(CompletedStepSummary c)
    {
        var declared = string.IsNullOrWhiteSpace(c.ExpectedArtifact) ? null : c.ExpectedArtifact.Trim();
        var reported = string.IsNullOrWhiteSpace(c.Outcome?.ArtifactRef) ? null : c.Outcome.ArtifactRef.Trim();
        var same = declared is not null && string.Equals(declared, reported, StringComparison.OrdinalIgnoreCase);
        return new ProbeTarget(
            c.Ordinal,
            Truncate(Flatten(c.Title)),
            declared is null ? null : Truncate(Flatten(declared)),
            same || reported is null ? null : Truncate(Flatten(reported)),
            same);
    }

    /// <summary>
    /// Probes both artifact channels against <paramref name="root"/>, emitting one fact line per step.
    /// Static + no logger on purpose: artifact names are user content, so this code cannot log them even
    /// by accident. Runs on a pool thread; bounded by the caps above.
    /// </summary>
    private static (string Facts, ArtifactProbeTally Tally) ProbeDeclarations(string root, List<ProbeTarget> targets)
    {
        var reportable = new List<ProbeTarget>(Math.Min(targets.Count, MaxReportedSteps));
        var skipped = 0;
        foreach (var t in targets)
        {
            if (reportable.Count >= MaxReportedSteps)
                skipped += (t.Declared is null ? 0 : 1) + (t.Reported is null ? 0 : 1);
            else
                reportable.Add(t);
        }

        var counters = new ProbeCounters();
        var found = new List<FoundArtifact>();
        var declaredOutcomes = new string?[reportable.Count];
        var reportedOutcomes = new string?[reportable.Count];

        // Two passes rather than both halves per step: the probe budget must go to the planner declarations
        // first, or adding the reported channel would push a later step's declaration past the cap.
        for (var i = 0; i < reportable.Count; i++)
        {
            if (reportable[i].Declared is { } declaration)
                declaredOutcomes[i] = ProbeOneDeclaration(root, declaration, counters, found, reportable[i].Ordinal);
        }
        for (var i = 0; i < reportable.Count; i++)
        {
            if (reportable[i].Reported is { } declaration)
                reportedOutcomes[i] = ProbeOneDeclaration(root, declaration, counters, found, reportable[i].Ordinal);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < reportable.Count; i++)
        {
            var t = reportable[i];
            var halves = new List<string>(2);
            if (declaredOutcomes[i] is { } declaredOutcome)
                halves.Add($"declared: {t.Declared} → {declaredOutcome}");
            if (reportedOutcomes[i] is { } reportedOutcome)
                halves.Add($"reported: {t.Reported} → {reportedOutcome}");
            sb.AppendLine($"- step {t.Ordinal + 1} \"{t.Title}\" {string.Join("; ", halves)}");
        }

        if (skipped > 0)
            sb.AppendLine($"- ({skipped} further declared artifact(s) not probed — probe budget reached)");

        var duplicates = DuplicateFactLines(root, found);
        foreach (var line in duplicates)
            sb.AppendLine(line);
        if (duplicates.Count > 0)
            sb.AppendLine(DuplicateHint);

        return (sb.ToString(), new ArtifactProbeTally(
            Declared: targets.Count(t => t.Declared is not null),
            Reported: targets.Count(t => t.Reported is not null),
            ReportedSame: targets.Count(t => t.ReportedSameAsDeclared),
            FileShaped: counters.FileShaped, NotFileShaped: counters.NotFileShaped,
            OverReportCap: skipped, Probed: counters.Probed, Found: counters.Found, NotFound: counters.NotFound,
            Folder: counters.Folder, Unresolvable: counters.Unresolvable, Uninspectable: counters.Uninspectable,
            VaultRef: counters.VaultRef, OverPathCap: counters.OverPathCap, DupPairs: duplicates.Count));
    }

    /// <summary>The outcome text for ONE declaration, shared by both channels.</summary>
    private static string ProbeOneDeclaration(
        string root, string declaration, ProbeCounters counters, List<FoundArtifact> found, int ordinal)
    {
        var candidates = FileCandidates(declaration);
        if (candidates.Count == 0)
        {
            counters.NotFileShaped++;
            return "not a file reference";
        }

        counters.FileShaped++;
        if (counters.Probed >= MaxProbedPaths)
        {
            counters.OverPathCap += candidates.Count;
            return "not probed (probe budget reached)";
        }

        var parts = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (counters.Probed >= MaxProbedPaths)
            {
                counters.OverPathCap++;
                parts.Add($"{candidate}: not probed (probe budget reached)");
                continue;
            }
            counters.Probed++;
            var (text, kind, resolved, size) = Probe(root, candidate);
            switch (kind)
            {
                case ProbeOutcome.Found: counters.Found++; break;
                case ProbeOutcome.NotFound: counters.NotFound++; break;
                case ProbeOutcome.Folder: counters.Folder++; break;
                case ProbeOutcome.Unresolvable: counters.Unresolvable++; break;
                case ProbeOutcome.Uninspectable: counters.Uninspectable++; break;
                case ProbeOutcome.VaultReference: counters.VaultRef++; break;
            }
            if (kind == ProbeOutcome.Found && resolved is not null)
                found.Add(new FoundArtifact(ordinal, candidate, resolved, size));
            // Don't echo the token when it IS the whole declaration — "report.md → found" reads
            // better than "report.md → report.md: found".
            parts.Add(candidates.Count == 1 && string.Equals(candidate, declaration, StringComparison.Ordinal)
                ? text
                : $"{candidate}: {text}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// One artifact fact. Resolution goes through the same containment guard as the file tools
    /// (<see cref="SafeFolderPath.TryResolveInsideAllowingAbsolute"/>, which canonicalizes and therefore
    /// rejects junction/symlink escapes), so the probe can never stat a path outside the folder and never
    /// follows a path escape. Metadata only — no file contents are read.
    /// </summary>
    private static (string Text, ProbeOutcome Kind, string? Resolved, long Size) Probe(string root, string candidate)
    {
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, candidate, out var resolved))
            return ("not a resolvable path inside the assistant files folder (not probed)", ProbeOutcome.Unresolvable, null, 0);

        try
        {
            var file = new FileInfo(resolved);
            if (file.Exists)
                return ($"found ({FormatSize(file.Length)}, modified {file.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z)", ProbeOutcome.Found, resolved, file.Length);
            if (Directory.Exists(resolved))
                return ("found, but it is a folder, not a file", ProbeOutcome.Folder, null, 0);
            // A step that obeyed the vault hint delivers to the vault, which is not part of the working
            // folder: calling that NOT FOUND would be a confident false fact about a correct run.
            if (IsVaultReference(candidate))
                return (VaultReferenceArm, ProbeOutcome.VaultReference, null, 0);
            return ("NOT FOUND", ProbeOutcome.NotFound, null, 0);
        }
        catch (Exception)
        {
            return ("not probed (could not be inspected)", ProbeOutcome.Uninspectable, null, 0);
        }
    }

    internal const string VaultReferenceArm = "names a vault reference — outside the working folder, not probed";

    private static bool IsVaultReference(string candidate) =>
        candidate.Replace('\\', '/').TrimStart('/')
            .StartsWith(VaultTargetPolicy.SourcesPrefix, StringComparison.OrdinalIgnoreCase);

    internal const string DuplicateHint =
        "A \"possible duplicate deliverable\" line is a HINT, not a finding: two steps each produced a similarly named and similarly sized file of the same type. Decide from the step results whether the plan called for both, or whether one step re-produced another step's deliverable under a new name.";

    /// <summary>
    /// Metadata-only near-duplicate hint over the artifacts that were actually found. Deliberately biased to
    /// MISS rather than to flag: a false duplicate on a correct run costs the critic a wrong sentence.
    /// </summary>
    private static List<string> DuplicateFactLines(string root, List<FoundArtifact> found)
    {
        var lines = new List<string>();
        for (var i = 0; i < found.Count && lines.Count < MaxDuplicatePairs; i++)
        {
            for (var j = i + 1; j < found.Count && lines.Count < MaxDuplicatePairs; j++)
            {
                var a = found[i];
                var b = found[j];
                if (a.Ordinal == b.Ordinal)
                    continue; // one step may legitimately produce several files
                if (string.Equals(a.Resolved, b.Resolved, StringComparison.OrdinalIgnoreCase))
                    continue; // two spellings of one file is one file
                if (!string.Equals(Path.GetExtension(a.Candidate), Path.GetExtension(b.Candidate), StringComparison.OrdinalIgnoreCase))
                    continue;
                var max = Math.Max(a.Size, b.Size);
                if (max == 0 || Math.Min(a.Size, b.Size) / (double)max < MinSizeRatio)
                    continue;
                if (!NameTokens(a.Candidate).Overlaps(NameTokens(b.Candidate)))
                    continue;
                if (IsScratch(root, a) || IsScratch(root, b))
                    continue; // working notes are not deliverables

                // Lower step first: the two channels are probed in separate passes, so list order is not step order.
                var (first, second) = a.Ordinal < b.Ordinal ? (a, b) : (b, a);
                lines.Add(
                    $"- possible duplicate deliverable: step {first.Ordinal + 1} \"{Truncate(Flatten(first.Candidate))}\" "
                    + $"({first.Size.ToString(CultureInfo.InvariantCulture)} B) and step {second.Ordinal + 1} "
                    + $"\"{Truncate(Flatten(second.Candidate))}\" ({second.Size.ToString(CultureInfo.InvariantCulture)} B) "
                    + "— same file type, similar size, overlapping names");
            }
        }
        return lines;
    }

    /// <summary>Name tokens long enough to mean something; a purely numeric run (a shared year) is not one.</summary>
    private static HashSet<string> NameTokens(string candidate)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = Path.GetFileNameWithoutExtension(candidate);
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && char.IsLetterOrDigit(name[i]))
                continue;
            if (i - start >= MinSharedTokenChars)
            {
                var token = name[start..i];
                if (token.Any(char.IsLetter))
                    tokens.Add(token);
            }
            start = i + 1;
        }
        return tokens;
    }

    /// <summary>The relative path is what <see cref="RunScratchFolder"/> takes — an absolute one silently no-ops.</summary>
    private static bool IsScratch(string root, FoundArtifact artifact)
    {
        string relative;
        try { relative = Path.GetRelativePath(root, artifact.Resolved); }
        catch (Exception) { relative = string.Empty; }
        return RunScratchFolder.Contains(relative);
    }

    /// <summary>
    /// Tolerant classification: <c>ExpectedArtifact</c> is planner free text ("a summary of the Q3
    /// numbers") as often as it is a filename, so only tokens that plausibly denote a FILE are probed —
    /// a token whose extension is a dot plus 2..5 letters/digits starting with a letter (".md" … ".xlsx").
    /// That deliberately ignores ".5" in "12.5" and ".0" in "v1.0", which would otherwise be reported as
    /// missing artifacts. Anything unclassified is reported as "not a file reference" — never as missing.
    /// </summary>
    private static List<string> FileCandidates(string declaration)
    {
        var result = new List<string>();
        foreach (var raw in declaration.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.TrimEnd(TrailingTrim);
            if (token.Length == 0 || !LooksLikeFileName(token))
                continue;
            if (result.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(token);
            if (result.Count >= MaxCandidatesPerDeclaration)
                break;
        }
        return result;
    }

    private static bool LooksLikeFileName(string token)
    {
        string ext;
        try { ext = Path.GetExtension(token); }
        catch { return false; }

        if (ext.Length is < 3 or > 6) return false; // ".md" … ".xlsx"; too short/long is prose, not a file
        if (!char.IsLetter(ext[1])) return false;   // kills ".5" / ".0" from decimals and version numbers
        for (var i = 2; i < ext.Length; i++)
            if (!char.IsLetterOrDigit(ext[i])) return false;

        return Path.GetFileNameWithoutExtension(token).Length > 0; // a bare ".md" is not a file reference
    }

    /// <summary>
    /// Keeps a declaration (and a step title) on ONE line. The block's value is that every line in it is a
    /// fact the app established; both fields are model/user text, so a newline inside either must not be
    /// able to forge an extra "- step N … → found" line.
    /// </summary>
    private static string Flatten(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static string Truncate(string text) =>
        text.Length <= MaxDeclarationChars ? text : text[..MaxDeclarationChars] + "…";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
    };
}
