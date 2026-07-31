using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Phase 3 R13 — the CHILD grant envelope. <c>AgentRunCreateRequest.PolicyJson</c> is opaque to the run
/// service, so a naive child-spawn would create a run with a NULL policy, and the resume reader then widens
/// null to the <c>{write_file}</c> floor — i.e. a child could end up WIDER than the parent that delegated to
/// it. <see cref="HeadlessRunLauncher.NarrowForChild"/> / <c>TrySerializeChildEnvelope</c> exist to make that
/// impossible, and these are their facts.
/// <para>
/// Pure static-helper coverage: no launcher instance, no <c>runsBaseDirOverride</c> harness, no disk. The
/// launcher-driving child facts (T-CHILD-1..4) belong to the group that adds <c>LaunchChildAsync</c>; nothing
/// spawns a child yet.
/// </para>
/// </summary>
public class HeadlessRunLauncherChildRunTests
{
    /// <summary>
    /// T-CHILD-ENV-1, GUARD. Containment in both directions of failure: the child's grant NAMES are a subset of
    /// the parent's, and the child's policy CLASSES are a subset of the parent's. Rows cover every unreadable
    /// shape the resume reader knows, plus the interactive empty envelope. Non-vacuity is asserted separately
    /// below (a "⊆" theory is trivially satisfied when everything is empty), on both halves.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("""{"v":99,"grantedWrites":["write_file","delete_file"]}""")]
    [InlineData("""{"v":1,"grantedWrites":["write_file"],"trigger":"Schedule"}""")]
    [InlineData("""{"v":1,"grantedWrites":["write_file","delete_file"],"trigger":"Schedule"}""")]
    [InlineData("""{"v":1,"grantedWrites":["write_file","edit_file","purge_cache"],"trigger":"Schedule"}""")]
    [InlineData("""{"v":1,"grantedWrites":[],"trigger":"Schedule","policy":{"autoApproveClasses":["Files","Todo"]}}""")]
    // The exact document ChatSessionManager persists for an interactive run when serialization faults: grants
    // nothing, so a child of it must also grant nothing.
    [InlineData("""{"v":1,"grantedWrites":[],"trigger":"User"}""")]
    public void AChildEnvelopeIsNeverWiderThanItsParents(string? parentPolicyJson)
    {
        // The parent's own effective grants, read exactly the way a resume reads them — including the floor a
        // parent with an unreadable envelope would itself fall back to. Comparing against the FLOOR rather than
        // against the empty set is the strict version of the claim: a child must not reach even that.
        var parentGrants = HeadlessRunLauncher.TryRestoreGrantEnvelope(parentPolicyJson)
                           ?? HeadlessRunRequest.DefaultGrantedWrites;
        var parentPolicy = HeadlessRunLauncher.TryRestorePolicy(parentPolicyJson);

        var (childGrants, childPolicy) = HeadlessRunLauncher.NarrowForChild(parentPolicyJson);

        Assert.All(childGrants, g => Assert.Contains(g, parentGrants, StringComparer.OrdinalIgnoreCase));
        Assert.All(
            childPolicy?.AutoApproveClasses ?? [],
            c => Assert.Contains(c, parentPolicy?.AutoApproveClasses ?? []));

        // And the serialized envelope the child would actually be created with restores no wider than that.
        var childJson = HeadlessRunLauncher.TrySerializeChildEnvelope(parentPolicyJson, AgentRunTrigger.Schedule);
        var restored = HeadlessRunLauncher.TryRestoreGrantEnvelope(childJson);
        Assert.NotNull(restored);
        Assert.All(restored!, g => Assert.Contains(g, parentGrants, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The theory's last row is a hand-copied literal because an attribute argument must be constant. Pinned
    /// against the production constant so the row cannot silently stop being the interactive envelope.
    /// </summary>
    [Fact]
    public void TheInteractiveEmptyEnvelopeRowMatchesTheProductionConstant()
        => Assert.Equal(
            HeadlessRunLauncher.InteractiveEmptyEnvelopeJson,
            """{"v":1,"grantedWrites":[],"trigger":"User"}""");

    /// <summary>
    /// T-CHILD-ENV-1's non-vacuity, GUARD. At least one row must produce a NON-EMPTY child grant set and one a
    /// non-empty child policy, or the containment theory above proves nothing: ⊆ holds for free over emptiness.
    /// </summary>
    [Fact]
    public void TheContainmentTheoryIsNotVacuous()
    {
        var (grants, _) = HeadlessRunLauncher.NarrowForChild(
            """{"v":1,"grantedWrites":["write_file"],"trigger":"Schedule"}""");
        Assert.Equal("write_file", Assert.Single(grants));

        var (_, policy) = HeadlessRunLauncher.NarrowForChild(
            """{"v":1,"grantedWrites":[],"trigger":"Schedule","policy":{"autoApproveClasses":["Files","Todo"]}}""");
        Assert.NotNull(policy);
        Assert.True(policy!.Covers(ToolClass.Files));
        Assert.True(policy.Covers(ToolClass.Todo));
    }

    /// <summary>
    /// T-CHILD-ENV-2, REGRESSION. An unreadable parent envelope grants the child NOTHING — not
    /// <c>DefaultGrantedWrites</c> and not the resume floor. The discriminating half is the round-trip: the
    /// serialized child envelope must restore to an EMPTY-BUT-PRESENT list, because the reader treats null as
    /// "apply the floor" and present-but-empty as "granted nothing". A helper that returned an empty list but
    /// persisted an unreadable document would still hand the child <c>{write_file}</c> at resume.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("""{"v":99,"grantedWrites":["write_file","delete_file"]}""")]
    [InlineData("""{"v":1,"policy":{"autoApproveClasses":["Files"]}}""")]
    public void AnUnreadableParentEnvelopeYieldsNoChildGrants_NotTheDefaultAndNotTheFloor(string? parentPolicyJson)
    {
        var (grants, policy) = HeadlessRunLauncher.NarrowForChild(parentPolicyJson);

        Assert.Empty(grants);
        Assert.Null(policy);

        var restored = HeadlessRunLauncher.TryRestoreGrantEnvelope(
            HeadlessRunLauncher.TrySerializeChildEnvelope(parentPolicyJson, AgentRunTrigger.Schedule));
        Assert.NotNull(restored);
        Assert.Empty(restored!);
    }

    /// <summary>
    /// T-CHILD-ENV-3, REGRESSION. A child is a delegate: it does the work it was asked for and it does not get
    /// to destroy anything, so a delete-like NAME is stripped even when the parent legitimately held it. The
    /// parent can still delete, in its own steps.
    /// </summary>
    [Fact]
    public void DeleteLikeGrantsAreStrippedEvenWhenTheParentHeldThem()
    {
        const string parent = """{"v":1,"grantedWrites":["write_file","delete_file"],"trigger":"Schedule"}""";

        // The parent really does hold both — otherwise this test would pass on a broken reader.
        Assert.Equal(new[] { "write_file", "delete_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(parent));

        var (grants, _) = HeadlessRunLauncher.NarrowForChild(parent);
        Assert.Equal("write_file", Assert.Single(grants));

        // A destructive MCP-shaped name the heuristic catches by stem, not by allowlist.
        var (byStem, _) = HeadlessRunLauncher.NarrowForChild(
            """{"v":1,"grantedWrites":["write_file","acme_purge_bucket"],"trigger":"Schedule"}""");
        Assert.Equal("write_file", Assert.Single(byStem));
    }

    /// <summary>
    /// T-CHILD-ENV-4, GUARD. The child envelope stays at <c>v:1</c> with additive members. The version check is
    /// an EXACT equality (<c>envelope.V != 1</c>), so a bump makes every persisted envelope unreadable at once —
    /// and for a grant list that is the FLOOR, i.e. a silent widening of every in-flight run.
    /// </summary>
    [Fact]
    public void TheChildEnvelopeStaysAtV1()
    {
        var childJson = HeadlessRunLauncher.TrySerializeChildEnvelope(
            """{"v":1,"grantedWrites":["write_file"],"trigger":"Schedule","policy":{"autoApproveClasses":["Files"]}}""",
            AgentRunTrigger.Schedule);

        Assert.Contains("\"v\":1", childJson);
        Assert.NotNull(HeadlessRunLauncher.TryRestoreGrantEnvelope(childJson));

        // The policy rides along unchanged — it is a class set that can never cover a delete-like tool, so
        // narrowing it would only stop the child doing the work it was delegated.
        var policy = HeadlessRunLauncher.TryRestorePolicy(childJson);
        Assert.NotNull(policy);
        Assert.True(policy!.Covers(ToolClass.Files));

        // Provenance only: the child's envelope records the PARENT's trigger and nothing reads it to widen a
        // grant. Pinned so the member is not quietly repurposed.
        Assert.Contains("\"trigger\":\"Schedule\"", childJson);
    }
}
