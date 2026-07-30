using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 04 D1/D10/D12: the autonomy policy rides inside the EXISTING <c>v:1</c> grant envelope as an additive
/// member. The version is deliberately not bumped — the reader compares with <c>!=</c>, so a bump would make
/// every pre-batch envelope unreadable, and for an interactive-origin envelope (<c>grantedWrites: []</c>) the
/// "restrictive" resume floor <c>{write_file}</c> is WIDER than the launch, i.e. a silent escalation.
/// </summary>
public class HeadlessRunLauncherPolicyTests
{
    private const string PreBatch04Envelope = """{"v":1,"grantedWrites":["write_file"],"trigger":"Schedule"}""";

    [Fact]
    public void PolicyRoundTripsInsideTheV1Envelope_WithoutBumpingV()
    {
        var policy = new RunAutonomyPolicy([ToolClass.Files, ToolClass.Todo]);

        var json = HeadlessRunLauncher.SerializeGrantEnvelope(["write_file"], AgentRunTrigger.Schedule, policy);

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"policy\"", json);
        Assert.Contains("\"autoApproveClasses\"", json);

        var restored = HeadlessRunLauncher.TryRestorePolicy(json);
        Assert.NotNull(restored);
        Assert.True(restored!.Covers(ToolClass.Files));
        Assert.True(restored.Covers(ToolClass.Todo));
        Assert.False(restored.Covers(ToolClass.Git));

        // The grant list still restores from the same document — the whole point of an additive member.
        Assert.Equal(new[] { "write_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(json));
    }

    [Fact]
    public void APreBatch04Envelope_HasNoPolicy_AndItsGrantsStillRestore()
    {
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(PreBatch04Envelope));
        Assert.Equal(new[] { "write_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(PreBatch04Envelope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("{\"v\":99,\"grantedWrites\":[\"write_file\"],\"policy\":{\"autoApproveClasses\":[\"Files\"]}}")]
    [InlineData("{\"somethingElse\":true}")]
    [InlineData("{\"v\":1,\"grantedWrites\":[\"write_file\"],\"policy\":{}}")]
    [InlineData("{\"v\":1,\"grantedWrites\":[\"write_file\"],\"policy\":{\"autoApproveClasses\":[]}}")]
    public void AnUnreadableEnvelopeLosesThePolicyBeforeItLosesTheGrantFloor(string? policyJson)
    {
        // D10's asymmetry: losing the policy is always the RESTRICTIVE direction, so it fails to null (today's
        // behaviour) rather than to a floor. The grant list is the one that needs a floor to work with.
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(policyJson));
    }

    [Fact]
    public void UnknownClassNamesAreDropped_NotHonoured()
    {
        var mixed = """{"v":1,"grantedWrites":[],"policy":{"autoApproveClasses":["Files","Warp","",null,"todo"]}}""";

        var restored = HeadlessRunLauncher.TryRestorePolicy(mixed);
        Assert.NotNull(restored);
        Assert.True(restored!.Covers(ToolClass.Files));
        Assert.True(restored.Covers(ToolClass.Todo));   // OrdinalIgnoreCase against the member names
        Assert.Equal(2, restored.AutoApproveClasses.Count);

        // No USABLE class ⇒ no policy at all, rather than an empty-but-present one.
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(
            """{"v":1,"grantedWrites":[],"policy":{"autoApproveClasses":["Warp"]}}"""));
        // "Unknown" is dropped like any unparseable name — Covers() hardcodes it false anyway, so carrying it
        // would only make the restored policy look wider than it is.
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(
            """{"v":1,"grantedWrites":[],"policy":{"autoApproveClasses":["Unknown"]}}"""));
    }

    [Fact]
    public void TheInteractiveFallbackLiteralIsTheDocumentTheSerializerProduces()
    {
        // SHAPE pin (D12): without this, a later member addition rots the literal silently while the
        // round-trip pin below still passes.
        Assert.Equal(
            HeadlessRunLauncher.InteractiveEmptyEnvelopeJson,
            HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, policy: null));

        // ROUND-TRIP pin: it restores an honoured-EMPTY grant set and NO policy, so an interactive run whose
        // envelope write faulted resumes granting nothing and auto-approving nothing.
        var grants = HeadlessRunLauncher.TryRestoreGrantEnvelope(HeadlessRunLauncher.InteractiveEmptyEnvelopeJson);
        Assert.NotNull(grants);
        Assert.Empty(grants!);
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(HeadlessRunLauncher.InteractiveEmptyEnvelopeJson));
    }

    [Fact]
    public void SerializeWithNoPolicy_OmitsTheMember()
    {
        // Not "policy":null — omitted, so a pre-04 reader sees a pre-04 document byte for byte.
        var json = HeadlessRunLauncher.SerializeGrantEnvelope(["write_file"], AgentRunTrigger.Schedule, policy: null);

        Assert.DoesNotContain("policy", json);
        Assert.Equal(PreBatch04Envelope, json);
    }

    [Fact]
    public void FromSettingsPreset_SerializesTheClassNames_AndExcludesGitAndExternal()
    {
        var policy = RunAutonomyPolicy.FromSettings(new AppSettings { AgentRunAutoApproveBuiltInWrites = true });
        var json = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, policy);

        var restored = HeadlessRunLauncher.TryRestorePolicy(json);
        Assert.NotNull(restored);
        Assert.True(restored!.Covers(ToolClass.Files));
        Assert.True(restored.Covers(ToolClass.Scheduling));
        Assert.False(restored.Covers(ToolClass.Git));
        Assert.False(restored.Covers(ToolClass.External));
        Assert.DoesNotContain("Git", json);
        Assert.DoesNotContain("External", json);
    }
}
