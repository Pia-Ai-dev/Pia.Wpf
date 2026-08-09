using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

// The resume reader widens a null policy to the {write_file} floor, so a naive child-spawn could end up WIDER than
// the parent that delegated to it — which is what NarrowForChild exists to prevent.
public class HeadlessRunLauncherChildRunTests
{
    // A "⊆" theory is trivially satisfied when everything is empty, so non-vacuity is asserted separately below.
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
        // The parent's grants read the way a resume reads them, floor included: comparing against the FLOOR rather
        // than against the empty set is the strict version of the claim.
        var parentGrants = HeadlessRunLauncher.TryRestoreGrantEnvelope(parentPolicyJson)
                           ?? HeadlessRunRequest.DefaultGrantedWrites;
        var parentPolicy = HeadlessRunLauncher.TryRestorePolicy(parentPolicyJson);

        var (childGrants, _, childPolicy) = HeadlessRunLauncher.NarrowForChild(parentPolicyJson);

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

    // The theory's last row is hand-copied because an attribute argument must be constant; pinned here so it
    // cannot silently stop being the interactive envelope.
    [Fact]
    public void TheInteractiveEmptyEnvelopeRowMatchesTheProductionConstant()
        => Assert.Equal(
            HeadlessRunLauncher.InteractiveEmptyEnvelopeJson,
            """{"v":1,"grantedWrites":[],"trigger":"User"}""");

    [Fact]
    public void TheContainmentTheoryIsNotVacuous()
    {
        var (grants, _, _) = HeadlessRunLauncher.NarrowForChild(
            """{"v":1,"grantedWrites":["write_file"],"trigger":"Schedule"}""");
        Assert.Equal("write_file", Assert.Single(grants));

        var (_, _, policy) = HeadlessRunLauncher.NarrowForChild(
            """{"v":1,"grantedWrites":[],"trigger":"Schedule","policy":{"autoApproveClasses":["Files","Todo"]}}""");
        Assert.NotNull(policy);
        Assert.True(policy!.Covers(ToolClass.Files));
        Assert.True(policy.Covers(ToolClass.Todo));
    }

    // A denial is a narrowing, so dropping it would let the delegate re-ask what the parent's person settled.
    [Fact]
    public void ParentDenialsPassToTheChildVerbatim()
    {
        const string parent = """{"v":1,"grantedWrites":[],"trigger":"Schedule","deniedWrites":["git_commit"]}""";

        var (_, denied, _) = HeadlessRunLauncher.NarrowForChild(parent);
        Assert.Equal("git_commit", Assert.Single(denied));

        var childJson = HeadlessRunLauncher.TrySerializeChildEnvelope(parent, AgentRunTrigger.Schedule);
        Assert.Equal("git_commit", Assert.Single(HeadlessRunLauncher.TryRestoreDeniedWritesEnvelope(childJson)));
    }

    // The round-trip is the discriminating half: the reader treats null as "apply the floor" and present-but-empty
    // as "granted nothing", so an empty list persisted as an unreadable document still grants {write_file}.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("""{"v":99,"grantedWrites":["write_file","delete_file"]}""")]
    [InlineData("""{"v":1,"policy":{"autoApproveClasses":["Files"]}}""")]
    public void AnUnreadableParentEnvelopeYieldsNoChildGrants_NotTheDefaultAndNotTheFloor(string? parentPolicyJson)
    {
        var (grants, _, policy) = HeadlessRunLauncher.NarrowForChild(parentPolicyJson);

        Assert.Empty(grants);
        Assert.Null(policy);

        var restored = HeadlessRunLauncher.TryRestoreGrantEnvelope(
            HeadlessRunLauncher.TrySerializeChildEnvelope(parentPolicyJson, AgentRunTrigger.Schedule));
        Assert.NotNull(restored);
        Assert.Empty(restored!);
    }

    // A child is a delegate: a delete-like NAME is stripped even when the parent legitimately held it.
    [Fact]
    public void DeleteLikeGrantsAreStrippedEvenWhenTheParentHeldThem()
    {
        const string parent = """{"v":1,"grantedWrites":["write_file","delete_file"],"trigger":"Schedule"}""";

        // The parent really does hold both — otherwise this test would pass on a broken reader.
        Assert.Equal(new[] { "write_file", "delete_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(parent));

        var (grants, _, _) = HeadlessRunLauncher.NarrowForChild(parent);
        Assert.Equal("write_file", Assert.Single(grants));

        // A destructive MCP-shaped name the heuristic catches by stem, not by allowlist.
        var (byStem, _, _) = HeadlessRunLauncher.NarrowForChild(
            """{"v":1,"grantedWrites":["write_file","acme_purge_bucket"],"trigger":"Schedule"}""");
        Assert.Equal("write_file", Assert.Single(byStem));
    }

    // The version check is an EXACT equality, so a bump makes every persisted envelope unreadable at once — and for
    // a grant list that means falling back to the FLOOR, a silent widening of every in-flight run.
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
