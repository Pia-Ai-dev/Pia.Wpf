using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>Test double for <see cref="IAgentVerifier"/>; an empty queue means ACCEPT, so orchestrator tests
/// that do not care about verdicts stay green.</summary>
internal sealed class FakeVerifier : IAgentVerifier
{
    public Queue<VerdictResult> Verdicts { get; } = new();
    public int VerifyCalls { get; private set; }
    public bool ThrowOnVerify { get; set; }

    /// <summary>Snapshot of <c>ctx.CompletedSteps</c> per verify call — a resumed run must not present only its
    /// post-resume slice to the critic.</summary>
    public List<IReadOnlyList<CompletedStepSummary>> SeenCompletedSteps { get; } = new();

    /// <summary>Verify runs on the ORCHESTRATOR thread, so this is the only place the run workspace root is
    /// observable once a step's finally has restored the ambient; recorded even when null.</summary>
    public List<string?> SeenWorkspaceRoots { get; } = new();

    /// <summary>The run-level persona and effort-stamped provider per verify call. On a RESUME this is the only
    /// place they are observable, since planning is skipped.</summary>
    public List<Persona> SeenPersonas { get; } = new();

    public List<AiProvider> SeenProviders { get; } = new();

    /// <summary>When set, the verify turn cancels this source (as ChatSession.Cancel() would) and then
    /// honors the linked run token — so the orchestrator's SafeVerify observes a genuine run cancel.</summary>
    public CancellationTokenSource? CancelSessionOnVerify { get; set; }

    /// <summary>Shared call log, appended with <c>"verify"</c> — the verify-then-promote-then-complete order
    /// takes more than one fake to observe.</summary>
    public List<string>? Order { get; set; }

    public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        VerifyCalls++;
        Order?.Add("verify");
        SeenCompletedSteps.Add(ctx.CompletedSteps.ToList());
        SeenWorkspaceRoots.Add(ctx.WorkspaceRoot);
        SeenPersonas.Add(persona);
        SeenProviders.Add(provider);
        if (CancelSessionOnVerify is { } src)
        {
            src.Cancel();               // user cancel fires during the in-flight verify turn
            ct.ThrowIfCancellationRequested(); // linked run ct now cancelled → OCE, like a real provider turn
        }
        if (ThrowOnVerify) throw new InvalidOperationException("verify boom");
        return Task.FromResult(Verdicts.Count > 0 ? Verdicts.Dequeue() : VerdictResult.Accept);
    }
}
