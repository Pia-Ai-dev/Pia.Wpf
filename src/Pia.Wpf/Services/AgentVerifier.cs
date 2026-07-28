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
        Func<FunctionCallContent, Task<object?>> toolHandler = call =>
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
            messages, provider, [EmitVerdictTool], toolHandler, mode: null, ct).ConfigureAwait(false))
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
        if (ctx.CompletedSteps.Count > 0)
        {
            sb.AppendLine("Steps executed (with their results, as reported by the assistant itself):");
            foreach (var c in ctx.CompletedSteps)
            {
                sb.AppendLine($"- [{(c.Succeeded ? "ok" : "failed")}] {c.Title}: {c.Intent}");
                if (!string.IsNullOrWhiteSpace(c.VisibleText))
                    sb.AppendLine($"    result: {c.VisibleText}");
                else if (c.FromEarlierSegment) // E2: a resumed run's pre-pause steps carry no result text
                    sb.AppendLine($"    result: {CompletedStepSummary.EarlierSegmentNote}");
            }
        }
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
            new(ChatRole.User, ctx.Goal),
        };
    }

    // ---- H1: declared-artifact probe (mechanical evidence for the verdict) ----

    internal const string ArtifactBlockHeader =
        "Declared-artifact probe — mechanical filesystem facts gathered by the app, NOT the assistant's claims:";

    // Hard bounds so a long plan can never stall the verify turn (the probe is pure metadata: no file
    // contents are read). A 48-step plan therefore costs at most MaxProbedPaths stat calls.
    private const int MaxProbedPaths = 12;
    private const int MaxReportedDeclarations = 20;
    private const int MaxCandidatesPerDeclaration = 3;
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
        Task<(string Facts, int Probed)>? probe = null;
        try
        {
            var declared = ctx.CompletedSteps.Where(c => !string.IsNullOrWhiteSpace(c.ExpectedArtifact)).ToList();
            if (declared.Count == 0)
                return null; // nothing was ever declared — no facts to add

            var root = await ResolveProbeRootAsync().ConfigureAwait(false);
            if (root is null)
            {
                _logger.LogInformation("Artifact probe skipped for {Count} declaration(s): no usable files folder.", declared.Count);
                return null;
            }

            // Off the caller's thread and time-boxed: the folder can be a slow or dead network share, and
            // a hung stat must never hold up the verify turn.
            probe = Task.Run(() => ProbeDeclarations(root, declared), CancellationToken.None);
            var (facts, probed) = await probe.WaitAsync(ProbeBudget, ct).ConfigureAwait(false);

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
    /// <c>FilesToolHandler.HandleToolCallAsync</c> does — an unattended run's workspace root when one is
    /// ambient, otherwise the configured assistant files folder (owner decision d1bf62d: unattended runs
    /// write there, so <c>WorkspaceRoot</c> is null in production and the settings folder IS the root the
    /// step writes landed in). Null when no usable folder exists. Canonicalizing here means a junction in
    /// the root path itself is not a hole in the containment check below.
    /// </summary>
    private async Task<string?> ResolveProbeRootAsync()
    {
        var ambientRoot = TaskAmbient.Current?.WorkspaceRoot;
        var configured = ambientRoot ?? (await _settings.GetSettingsAsync().ConfigureAwait(false)).AssistantFilesFolder;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var full = Path.GetFullPath(configured);
        if (!Directory.Exists(full))
            return null;
        return SafeFolderPath.Canonicalize(full);
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

            var declaration = Truncate(c.ExpectedArtifact!.Trim());
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

            sb.AppendLine($"- step {c.Ordinal + 1} \"{c.Title}\" declared: {declaration} → {outcome}");
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

    private static string Truncate(string text) =>
        text.Length <= MaxDeclarationChars ? text : text[..MaxDeclarationChars] + "…";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
    };
}
