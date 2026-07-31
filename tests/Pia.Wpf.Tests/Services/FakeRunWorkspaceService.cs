using System.IO;
using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>
/// Substitutable <see cref="IRunWorkspaceService"/> for launcher/orchestrator tests: it records every call
/// and can be told to fail provisioning, so "a provisioning failure must not fail the run" (Batch 06 B16) is
/// assertable without a real filesystem-level fault. It creates the workspace directory on a successful
/// provision, because the caller hands that path to the executor and the file tools require it to exist.
/// </summary>
internal sealed class FakeRunWorkspaceService : IRunWorkspaceService
{
    private readonly string _runsBase;

    public FakeRunWorkspaceService(string runsBase) => _runsBase = runsBase;

    /// <summary>When false, <see cref="ProvisionAsync"/> returns null — the "no isolation" degrade.</summary>
    public bool ProvisionSucceeds { get; set; } = true;

    public RunWorkspaceMode Mode { get; set; } = RunWorkspaceMode.Copy;

    public string? BranchName { get; set; }

    public List<Guid> Provisioned { get; } = [];
    public List<Guid> TornDown { get; } = [];
    public List<Guid> Promoted { get; } = [];
    public int OrphanSweeps { get; private set; }

    /// <summary>
    /// What <see cref="PromoteAsync"/> hands back. Null (the default) is the real service's "nothing was
    /// promoted, the workspace is intact" degrade, which is what the pre-promotion tests want; set a result
    /// to exercise the promote-then-tear-down path.
    /// </summary>
    public RunPromotionResult? PromoteResult { get; set; }

    /// <summary>When set, <see cref="PromoteAsync"/> throws — the failure-isolation probe. The real service
    /// swallows its own faults, so this models a fault the wrapper must still contain.</summary>
    public bool ThrowOnPromote { get; set; }

    public RunWorkspaceOutcome? Outcome { get; set; }

    /// <summary>When set, <see cref="DescribeAsync"/> returns null — "this run has no workspace", which is what
    /// a cleanly promoted run looks like (it was torn down at promotion).</summary>
    public bool DescribeReturnsNothing { get; set; }

    /// <summary>How many times the outcome was described. The panel must ask only about a SETTLED run, so a
    /// count of zero is the observable form of "the read did not land on the projection path".</summary>
    public int Describes { get; private set; }

    /// <summary>
    /// Shared call log a test can pass in to assert ORDER across services — "verify, then promote, then
    /// complete" is the whole point of Batch 06 B8 and is not observable from any single fake.
    /// </summary>
    public List<string>? Order { get; set; }

    /// <summary>Signalled the first time <see cref="TearDownAsync"/> is called, so a fact can await a
    /// fire-and-forget teardown instead of blocking on it (xUnit1031: never <c>.Result</c>/<c>.Wait()</c>).</summary>
    public TaskCompletionSource TornDownOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string RootFor(Guid runId) => Path.Combine(_runsBase, runId.ToString());

    public Task<RunWorkspace?> ProvisionAsync(Guid runId, string? workingSubpath, CancellationToken ct)
    {
        Provisioned.Add(runId);
        if (!ProvisionSucceeds)
            return Task.FromResult<RunWorkspace?>(null);

        var root = RootFor(runId);
        Directory.CreateDirectory(root);
        return Task.FromResult<RunWorkspace?>(new RunWorkspace(runId, root, Mode, _runsBase, BranchName));
    }

    public Task<RunPromotionResult?> PromoteAsync(Guid runId, CancellationToken ct)
    {
        Promoted.Add(runId);
        Order?.Add("promote");
        if (ThrowOnPromote)
            throw new InvalidOperationException("promote boom");
        return Task.FromResult(PromoteResult);
    }

    public Task<RunWorkspaceOutcome?> DescribeAsync(Guid runId, CancellationToken ct)
    {
        Describes++;
        return Task.FromResult(DescribeReturnsNothing
            ? null
            : Outcome ?? new RunWorkspaceOutcome(Mode, BranchName, HasUnpublishedFiles: false));
    }

    public Task TearDownAsync(Guid runId, CancellationToken ct)
    {
        TornDown.Add(runId);
        Order?.Add("teardown");
        TornDownOnce.TrySetResult();
        try { if (Directory.Exists(RootFor(runId))) Directory.Delete(RootFor(runId), recursive: true); } catch { }
        return Task.CompletedTask;
    }

    public Task SweepOrphanMetadataAsync(CancellationToken ct)
    {
        OrphanSweeps++;
        return Task.CompletedTask;
    }
}
