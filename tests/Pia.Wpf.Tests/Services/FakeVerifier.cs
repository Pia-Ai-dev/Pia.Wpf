using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;

namespace Pia.Tests.Services;

/// <summary>Test double for <see cref="IAgentVerifier"/>. Default (empty queue) = ACCEPT so existing
/// orchestrator tests stay green; enqueue verdicts to drive verify-fail flows; ThrowOnVerify exercises
/// the degrade-to-accept guardrail.</summary>
internal sealed class FakeVerifier : IAgentVerifier
{
    public Queue<VerdictResult> Verdicts { get; } = new();
    public int VerifyCalls { get; private set; }
    public bool ThrowOnVerify { get; set; }

    public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        VerifyCalls++;
        if (ThrowOnVerify) throw new InvalidOperationException("verify boom");
        return Task.FromResult(Verdicts.Count > 0 ? Verdicts.Dequeue() : VerdictResult.Accept);
    }
}
