using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;

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

    /// <summary>When set, the verify turn cancels this source (as ChatSession.Cancel() would) and then
    /// honors the linked run token — so the orchestrator's SafeVerify observes a genuine run cancel.</summary>
    public CancellationTokenSource? CancelSessionOnVerify { get; set; }

    public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        VerifyCalls++;
        SeenCompletedSteps.Add(ctx.CompletedSteps.ToList());
        if (CancelSessionOnVerify is { } src)
        {
            src.Cancel();               // user cancel fires during the in-flight verify turn
            ct.ThrowIfCancellationRequested(); // linked run ct now cancelled → OCE, like a real provider turn
        }
        if (ThrowOnVerify) throw new InvalidOperationException("verify boom");
        return Task.FromResult(Verdicts.Count > 0 ? Verdicts.Dequeue() : VerdictResult.Accept);
    }
}
