using System.IO;
using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>A successful provision creates the directory, because the file tools require the path to exist.</summary>
internal sealed class FakeRunWorkspaceService : IRunWorkspaceService
{
    private readonly string _runsBase;

    public FakeRunWorkspaceService(string runsBase) => _runsBase = runsBase;

    /// <summary>When false, <see cref="ProvisionAsync"/> returns null — the "no isolation" degrade.</summary>
    public bool ProvisionSucceeds { get; set; } = true;

    public RunWorkspaceMode Mode { get; set; } = RunWorkspaceMode.Copy;

    public string? BranchName { get; set; }

    public List<Guid> Provisioned { get; } = [];

    /// <summary>The interactive path passes the chat's working directory; an unattended run passes null.</summary>
    public List<string?> ProvisionedSubpaths { get; } = [];
    public List<Guid> TornDown { get; } = [];
    public List<Guid> Promoted { get; } = [];
    public int OrphanSweeps { get; private set; }

    /// <summary>Null (the default) is the real service's "nothing promoted, workspace intact" degrade.</summary>
    public RunPromotionResult? PromoteResult { get; set; }

    /// <summary>Models a fault the wrapper must contain; the real service swallows its own.</summary>
    public bool ThrowOnPromote { get; set; }

    public RunWorkspaceOutcome? Outcome { get; set; }

    /// <summary>"No workspace" — what a cleanly promoted COPY-mode run looks like, torn down at promotion.</summary>
    public bool DescribeReturnsNothing { get; set; }

    /// <summary>Zero is the observable form of "the read never landed on the projection path".</summary>
    public int Describes { get; private set; }

    /// <summary>Shared call log for asserting order across services, which no single fake can observe.</summary>
    public List<string>? Order { get; set; }

    /// <summary>Lets a test await a fire-and-forget teardown instead of blocking on it (xUnit1031).</summary>
    public TaskCompletionSource TornDownOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string RootFor(Guid runId) => Path.Combine(_runsBase, runId.ToString());

    public Task<RunWorkspace?> ProvisionAsync(Guid runId, string? workingSubpath, CancellationToken ct)
    {
        Provisioned.Add(runId);
        ProvisionedSubpaths.Add(workingSubpath);
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
