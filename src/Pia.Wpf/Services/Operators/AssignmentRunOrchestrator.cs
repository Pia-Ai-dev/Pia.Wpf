using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Shared.Operators;

namespace Pia.Services.Operators;

/// <summary>
/// The one path content takes out of, and back into, the encrypted plane.
///
/// Two orderings are load-bearing and neither is a matter of taste:
/// <list type="bullet">
/// <item>Consent, then read. A receipt is a required argument, so nothing local is opened — let alone
/// sent — until a human affirmed this exact selection and the record of that hit disk.</item>
/// <item>Commit, then acknowledge. The artifact is written as a local assistant chat BEFORE
/// <c>collect</c>, because collect is irreversible: acknowledging first and then failing the local write
/// destroys the result with nothing left to fetch.</item>
/// </list>
/// </summary>
public interface IAssignmentRunOrchestrator
{
    /// <summary>
    /// Reads the consented records, sends them, and remembers the run. <paramref name="receipt"/> has no
    /// default and is checked against the consent log, so a background caller cannot reach this by leaving an
    /// argument out — it would have to write a consent record claiming a user affirmed something.
    /// </summary>
    Task<AssignmentStartOutcome> StartAsync(
        AssignmentRequest request, AssignmentConsentReceipt receipt, CancellationToken ct = default);

    /// <summary>
    /// Advances every run this device is still waiting on: finished ones are pulled, written locally as an
    /// assistant chat and only then acknowledged. Safe to call repeatedly — the chat id was minted before the
    /// run started, so a re-pull overwrites its own chat instead of making a second one.
    /// </summary>
    Task<int> DrainAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IAssignmentRunOrchestrator"/>
public sealed class AssignmentRunOrchestrator : IAssignmentRunOrchestrator
{
    /// <summary>
    /// How long a run may go unanswered before this device stops waiting for it. Comfortably past the server's
    /// default plaintext window (72 hours) so a laptop closed for a long weekend still collects its result, and
    /// far short of the row's own retention, because by then there is nothing to collect: the artifact goes
    /// with the plaintext.
    /// </summary>
    internal static readonly TimeSpan AbandonAfter = TimeSpan.FromDays(7);

    private readonly IAssignmentApiClient _api;
    private readonly IAssignmentConsentStore _consent;
    private readonly IAssignmentScopeResolver _scope;
    private readonly IAssignmentPendingStore _pending;
    private readonly IAssistantChatService _chats;
    private readonly ILogger<AssignmentRunOrchestrator> _logger;

    public AssignmentRunOrchestrator(
        IAssignmentApiClient api,
        IAssignmentConsentStore consent,
        IAssignmentScopeResolver scope,
        IAssignmentPendingStore pending,
        IAssistantChatService chats,
        ILogger<AssignmentRunOrchestrator> logger)
    {
        _api = api;
        _consent = consent;
        _scope = scope;
        _pending = pending;
        _chats = chats;
        _logger = logger;
    }

    /// <summary>Raised once a run has been pulled, written locally and acknowledged, so a surface can tell the
    /// user their result arrived. Carries ids only.</summary>
    public event EventHandler<AssignmentCompleted>? Completed;

    public async Task<AssignmentStartOutcome> StartAsync(
        AssignmentRequest request, AssignmentConsentReceipt receipt, CancellationToken ct = default)
    {
        // Before anything is read. A receipt this process's consent log did not write is not a receipt: the
        // whole point is that a human affirmed THIS selection a moment ago.
        if (!_consent.WasRecorded(receipt.RecordId) || !SelectionMatches(request, receipt))
        {
            _logger.LogWarning(
                "Refusing to start an assignment: the consent receipt is missing or does not match the request.");
            return AssignmentStartOutcome.ConsentMissing;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt) ||
            request.Prompt.Length > AssignmentInput.MaxPromptChars ||
            request.Items.Count > AssignmentInput.MaxItems ||
            request.Items.Any(i => i.ExceedsItemCap) ||
            request.Items.Sum(i => i.CharCount) > AssignmentInput.MaxTotalItemChars)
        {
            // Refused against the same constants the server enforces, so the round trip never happens and the
            // user is not told "too large" by a 400 they cannot act on.
            _logger.LogInformation("The assignment selection is over a published cap; not sending it.");
            return AssignmentStartOutcome.TooLarge;
        }

        var items = new List<AssignmentInputItem>(request.Items.Count);
        foreach (var item in request.Items)
        {
            ct.ThrowIfCancellationRequested();
            var text = await _scope.ReadTextAsync(item, ct);
            if (string.IsNullOrEmpty(text))
            {
                // Deleted between the consent screen and here. Dropped rather than substituted: the user
                // consented to a record, not to a placeholder.
                _logger.LogInformation(
                    "A consented {EntityType} record is no longer readable; leaving it out.", item.EntityType);
                continue;
            }

            items.Add(new AssignmentInputItem(item.EntityType, item.EntityId, item.Title, text, item.UpdatedAt));
        }

        var envelope = new AssignmentInput(AssignmentInput.CurrentSchemaVersion, request.Prompt, items);
        if (envelope.Items.Sum(i => i.Text.Length) > AssignmentInput.MaxTotalItemChars)
        {
            // The listed char counts are a moment old; the content just read is what actually counts.
            _logger.LogInformation("The consented records grew past the total cap since they were listed.");
            return AssignmentStartOutcome.TooLarge;
        }

        var assignmentId = await _api.CreateAsync(request.SkillName, envelope, ct);
        if (assignmentId is null) return AssignmentStartOutcome.Refused;

        await _pending.AddAsync(new PendingAssignment(
            assignmentId.Value, Guid.NewGuid(), request.SkillName, request.Prompt, DateTime.UtcNow));

        _logger.LogInformation(
            "Started assignment {AssignmentId} on '{Skill}' with {ItemCount} consented record(s) under consent {RecordId}.",
            assignmentId, request.SkillName, items.Count, receipt.RecordId);

        return new AssignmentStartOutcome(AssignmentStartStatus.Started, assignmentId);
    }

    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var pending = await _pending.GetAllAsync();
        if (pending.Count == 0) return 0;

        var finished = 0;
        foreach (var run in pending)
        {
            ct.ThrowIfCancellationRequested();

            var assignment = await _api.GetAsync(run.AssignmentId, ct);
            if (assignment is null)
            {
                // Unreachable or gone, and this cannot tell which — so the age bound is what stops a row the
                // server has since deleted from leaving an entry that is polled for ever. Nothing is lost by
                // giving up on it: past the plaintext window the input AND the artifact are already dropped,
                // so there was never anything left to collect.
                if (run.StartedAtUtc < DateTime.UtcNow - AbandonAfter)
                {
                    await _pending.RemoveAsync(run.AssignmentId);
                    _logger.LogWarning(
                        "Giving up on assignment {AssignmentId}: it started {Days} day(s) ago and the server no " +
                        "longer answers for it, so its result is gone.",
                        run.AssignmentId, (int)(DateTime.UtcNow - run.StartedAtUtc).TotalDays);
                }

                continue;
            }

            if (!IsTerminal(assignment.Status)) continue;

            // COMMIT FIRST. If this throws, the pending entry survives and collect is never sent, so the
            // server still holds the artifact for the next attempt.
            await WriteArtifactChatAsync(run, assignment, ct);

            // Only now. A 204 or a 404 both mean the server has nothing left to hand over.
            if (!await _api.CollectAsync(run.AssignmentId, ct))
            {
                _logger.LogInformation(
                    "Assignment {AssignmentId} is stored locally but not yet acknowledged; retrying next pass.",
                    run.AssignmentId);
                continue;
            }

            await _pending.RemoveAsync(run.AssignmentId);
            finished++;
            Completed?.Invoke(this, new AssignmentCompleted(
                run.AssignmentId, run.ChatId, run.SkillName,
                Succeeded: string.Equals(assignment.Status, "Completed", StringComparison.Ordinal)));
        }

        return finished;
    }

    /// <summary>
    /// The artifact re-enters the encrypted plane as an ordinary assistant chat: the chat sync worker already
    /// encrypts it and pushes it, and the server refuses a plaintext chat write for an E2EE account — so this
    /// one call is the whole re-encryption. It also makes a second device work for free, through ciphertext
    /// chat sync, even after the backend plaintext is gone.
    /// </summary>
    private async Task WriteArtifactChatAsync(
        PendingAssignment run, AssignmentDto assignment, CancellationToken ct)
    {
        var answer = assignment.ArtifactText
            ?? $"This background assignment finished without a result ({assignment.ErrorCode ?? assignment.Status}).";

        var now = DateTime.UtcNow;
        var chat = new SyncAssistantChat
        {
            Id = run.ChatId,
            Title = Title(run.Prompt),
            CreatedAt = run.StartedAtUtc,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = run.Prompt,
                    Timestamp = run.StartedAtUtc,
                },
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = answer,
                    Timestamp = now,
                },
            ],
        };

        await _chats.SaveAsync(chat, ct);
        _logger.LogInformation(
            "Stored the artifact of assignment {AssignmentId} as chat {ChatId}.", run.AssignmentId, run.ChatId);
        _logger.SensitiveDebug("Artifact for {AssignmentId}: {Answer}", run.AssignmentId, answer);
    }

    /// <summary>The receipt has to be about the request being made, not merely exist — otherwise a stale one
    /// for a small selection would authorise a larger one.</summary>
    private static bool SelectionMatches(AssignmentRequest request, AssignmentConsentReceipt receipt) =>
        string.Equals(request.SkillName, receipt.SkillName, StringComparison.Ordinal)
        && request.Items.Count == receipt.Items.Count
        && request.Items.All(i => receipt.Items.Any(
            r => r.EntityId == i.EntityId && string.Equals(r.EntityType, i.EntityType, StringComparison.Ordinal)));

    private static bool IsTerminal(string status) =>
        status is "Completed" or "Failed" or "Cancelled";

    private static string Title(string prompt)
    {
        var line = prompt.Split('\n', 2)[0].Trim();
        if (line.Length == 0) return "Background assignment";
        return line.Length <= 60 ? line : line[..60];
    }
}

/// <summary>A finished run, once it is stored locally AND acknowledged. Ids and a flag only — the result itself
/// is the chat it was written to.</summary>
public sealed record AssignmentCompleted(Guid AssignmentId, Guid ChatId, string SkillName, bool Succeeded);
