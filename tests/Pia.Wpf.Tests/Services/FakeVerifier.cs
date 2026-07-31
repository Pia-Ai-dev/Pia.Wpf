using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>Test double for <see cref="IAgentVerifier"/>. Default (empty queue) = ACCEPT so existing
/// orchestrator tests stay green; enqueue verdicts to drive verify-fail flows; ThrowOnVerify exercises
/// the degrade-to-accept guardrail; CancelSessionOnVerify models a user cancel landing mid-verify.</summary>
internal sealed class FakeVerifier : IAgentVerifier
{
    public Queue<VerdictResult> Verdicts { get; } = new();
    public int VerifyCalls { get; private set; }
    public bool ThrowOnVerify { get; set; }

    /// <summary>
    /// Snapshot of <c>ctx.CompletedSteps</c> per verify call — what the critic actually got to judge
    /// (E2: a resumed run must not present only its post-resume slice).
    /// </summary>
    public List<IReadOnlyList<CompletedStepSummary>> SeenCompletedSteps { get; } = new();

    /// <summary>
    /// Snapshot of <c>ctx.WorkspaceRoot</c> per verify call — the isolated run workspace the executor
    /// published in BeginRunAsync (Batch 06 B3). Verify runs on the ORCHESTRATOR thread, outside any
    /// step's ambient, so this is the only place the root is observable after a step's finally has
    /// restored the ambient; the resume half of G2 reads its call site through it. Recorded even when
    /// null, so a test can tell "no isolation" apart from "verify never ran".
    /// </summary>
    public List<string?> SeenWorkspaceRoots { get; } = new();

    /// <summary>When set, the verify turn cancels this source (as ChatSession.Cancel() would) and then
    /// honors the linked run token — so the orchestrator's SafeVerify observes a genuine run cancel.</summary>
    public CancellationTokenSource? CancelSessionOnVerify { get; set; }

    public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        VerifyCalls++;
        SeenCompletedSteps.Add(ctx.CompletedSteps.ToList());
        SeenWorkspaceRoots.Add(ctx.WorkspaceRoot);
        if (CancelSessionOnVerify is { } src)
        {
            src.Cancel();               // user cancel fires during the in-flight verify turn
            ct.ThrowIfCancellationRequested(); // linked run ct now cancelled → OCE, like a real provider turn
        }
        if (ThrowOnVerify) throw new InvalidOperationException("verify boom");
        return Task.FromResult(Verdicts.Count > 0 ? Verdicts.Dequeue() : VerdictResult.Accept);
    }
}
