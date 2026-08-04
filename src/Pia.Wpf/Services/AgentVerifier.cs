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
/// completed step's declared <c>ExpectedArtifact</c> is probed against the run's effective file root
/// and the found/not-found facts are fed to the critic as a distinct block. The probe is bounded and
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
    private const int MaxReportedDeclarations = 20;
    private const int MaxCandidatesPerDeclaration = 3;

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
        Task<(string? Facts, int Probed)>? probe = null;
        try
        {
            var declared = ctx.CompletedSteps.Where(c => !string.IsNullOrWhiteSpace(c.ExpectedArtifact)).ToList();
            if (declared.Count == 0)
                return null; // nothing was ever declared — no facts to add

            // Only the settings read happens here (a local DB read, not a filesystem walk). Everything that
            // TOUCHES the filesystem — including resolving the root — runs inside the time-boxed task below.
            // ctx FIRST: verify runs on the orchestrator thread where the per-step ambient is already
            // restored (Batch 06 B3). The ambient read is kept as the second choice for any caller that DOES
            // verify inside a step flow; the settings folder stays the last resort.
            var ambientRoot = ctx.WorkspaceRoot ?? TaskAmbient.Current?.WorkspaceRoot;
            var configured = ambientRoot ?? (await _settings.GetSettingsAsync().ConfigureAwait(false)).AssistantFilesFolder;
            if (string.IsNullOrWhiteSpace(configured))
            {
                _logger.LogInformation("Artifact probe skipped for {Count} declaration(s): no usable files folder.", declared.Count);
                return null;
            }

            var workingSubpath = ctx.WorkingSubpath;

            // Off the caller's thread and time-boxed: the folder can be a slow or dead network share, and
            // a hung stat must never hold up the verify turn. The ROOT RESOLUTION is inside the box on
            // purpose — Directory.Exists/Canonicalize on a dead UNC path is exactly the call that blocks
            // (for the SMB connect timeout, tens of seconds), so resolving it before the box would leave
            // the advertised budget covering only the cheap part.
            probe = Task.Run<(string? Facts, int Probed)>(() =>
            {
                var root = ResolveProbeRoot(configured, workingSubpath);
                return root is null ? (null, 0) : ProbeDeclarations(root, declared);
            }, CancellationToken.None);
            var (facts, probed) = await probe.WaitAsync(ProbeBudget, ct).ConfigureAwait(false);
            if (facts is null)
            {
                _logger.LogInformation("Artifact probe skipped for {Count} declaration(s): files folder does not exist.", declared.Count);
                return null;
            }

            _logger.LogInformation("Artifact probe: {Declared} declaration(s), {Probed} path(s) probed.", declared.Count, probed);
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
    private static string? ResolveProbeRoot(string configured, string? workingSubpath)
    {
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

        return root;
    }

    /// <summary>
    /// Probes the declared artifacts against <paramref name="root"/>, emitting one fact line per
    /// declaration. Static + no logger on purpose: artifact names are user content, so this code cannot
    /// log them even by accident. Runs on a pool thread; bounded by the caps above.
    /// </summary>
    private static (string Facts, int Probed) ProbeDeclarations(string root, List<CompletedStepSummary> declared)
    {
        var sb = new StringBuilder();
        var probed = 0;
        var reported = 0;
        var skipped = 0;

        foreach (var c in declared)
        {
            if (reported >= MaxReportedDeclarations) { skipped++; continue; }
            reported++;

            var declaration = Truncate(Flatten(c.ExpectedArtifact!.Trim()));
            var candidates = FileCandidates(declaration);

            string outcome;
            if (candidates.Count == 0)
            {
                outcome = "not a file reference";
            }
            else if (probed >= MaxProbedPaths)
            {
                outcome = "not probed (probe budget reached)";
            }
            else
            {
                var parts = new List<string>(candidates.Count);
                foreach (var candidate in candidates)
                {
                    if (probed >= MaxProbedPaths)
                    {
                        parts.Add($"{candidate}: not probed (probe budget reached)");
                        continue;
                    }
                    probed++;
                    var result = Probe(root, candidate);
                    // Don't echo the token when it IS the whole declaration — "report.md → found" reads
                    // better than "report.md → report.md: found".
                    parts.Add(candidates.Count == 1 && string.Equals(candidate, declaration, StringComparison.Ordinal)
                        ? result
                        : $"{candidate}: {result}");
                }
                outcome = string.Join("; ", parts);
            }

            // BOTH interpolated fields are model text and BOTH are sanitized: the step title is planner
            // free text (AgentPlanner only trims it), so an embedded newline in it could otherwise forge a
            // second "- step N … → found" line inside a block the prompt introduces as mechanical
            // app-gathered facts. Truncate also bounds an over-long title.
            sb.AppendLine($"- step {c.Ordinal + 1} \"{Truncate(Flatten(c.Title))}\" declared: {declaration} → {outcome}");
        }

        if (skipped > 0)
            sb.AppendLine($"- ({skipped} further declared artifact(s) not probed — probe budget reached)");

        return (sb.ToString(), probed);
    }

    /// <summary>
    /// One artifact fact. Resolution goes through the same containment guard as the file tools
    /// (<see cref="SafeFolderPath.TryResolveInsideAllowingAbsolute"/>, which canonicalizes and therefore
    /// rejects junction/symlink escapes), so the probe can never stat a path outside the folder and never
    /// follows a path escape. Metadata only — no file contents are read.
    /// </summary>
    private static string Probe(string root, string candidate)
    {
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, candidate, out var resolved))
            return "not a resolvable path inside the assistant files folder (not probed)";

        try
        {
            var file = new FileInfo(resolved);
            if (file.Exists)
                return $"found ({FormatSize(file.Length)}, modified {file.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z)";
            if (Directory.Exists(resolved))
                return "found, but it is a folder, not a file";
            return "NOT FOUND";
        }
        catch (Exception)
        {
            return "not probed (could not be inspected)";
        }
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
