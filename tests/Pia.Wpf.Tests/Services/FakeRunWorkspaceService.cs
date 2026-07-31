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
        return Task.FromResult<RunPromotionResult?>(null);
    }

    public Task<RunWorkspaceOutcome?> DescribeAsync(Guid runId, CancellationToken ct)
        => Task.FromResult<RunWorkspaceOutcome?>(new RunWorkspaceOutcome(Mode, BranchName, HasUnpublishedFiles: false));

    public Task TearDownAsync(Guid runId, CancellationToken ct)
    {
        TornDown.Add(runId);
        try { if (Directory.Exists(RootFor(runId))) Directory.Delete(RootFor(runId), recursive: true); } catch { }
        return Task.CompletedTask;
    }

    public Task SweepOrphanMetadataAsync(CancellationToken ct)
    {
        OrphanSweeps++;
        return Task.CompletedTask;
    }
}
