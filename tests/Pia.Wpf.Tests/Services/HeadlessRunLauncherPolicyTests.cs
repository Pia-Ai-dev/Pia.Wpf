using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The envelope version is deliberately not bumped — the reader compares with <c>!=</c>, so a bump would make every existing envelope unreadable.</summary>
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
    // A truncated / hand-edited column: policy present, grantedWrites ABSENT.
    [InlineData("{\"v\":1,\"policy\":{\"autoApproveClasses\":[\"Files\",\"External\"]}}")]
    public void AnUnreadableEnvelopeLosesThePolicyBeforeItLosesTheGrantFloor(string? policyJson)
    {
        // Losing the policy is always the restrictive direction, so it fails to null rather than to a floor.
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(policyJson));
    }

    /// <summary>Both halves must treat a missing <c>grantedWrites</c> as unreadable, or a resumed run auto-approves the named classes with no grant behind it.</summary>
    [Fact]
    public void ADocumentWithNoGrantedWrites_IsUnreadableToBothHalvesOfTheReader()
    {
        const string truncated = """{"v":1,"policy":{"autoApproveClasses":["Files","External"]}}""";

        Assert.Null(HeadlessRunLauncher.TryRestoreGrantEnvelope(truncated));
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(truncated));

        // The same document WITH grantedWrites is readable by both, so the discriminator is that member.
        const string whole = """{"v":1,"grantedWrites":[],"policy":{"autoApproveClasses":["Files"]}}""";
        Assert.Empty(HeadlessRunLauncher.TryRestoreGrantEnvelope(whole)!);
        Assert.True(HeadlessRunLauncher.TryRestorePolicy(whole)!.Covers(ToolClass.Files));
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
        // "Unknown" is dropped like any unparseable name — Covers() hardcodes it false anyway.
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(
            """{"v":1,"grantedWrites":[],"policy":{"autoApproveClasses":["Unknown"]}}"""));
    }

    [Fact]
    public void TheInteractiveFallbackLiteralIsTheDocumentTheSerializerProduces()
    {
        // Shape pin: a later member addition would rot the literal silently while the round-trip pin passes.
        Assert.Equal(
            HeadlessRunLauncher.InteractiveEmptyEnvelopeJson,
            HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, policy: null));

        // Round-trip pin: an interactive run whose envelope write faulted resumes granting nothing.
        var grants = HeadlessRunLauncher.TryRestoreGrantEnvelope(HeadlessRunLauncher.InteractiveEmptyEnvelopeJson);
        Assert.NotNull(grants);
        Assert.Empty(grants!);
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(HeadlessRunLauncher.InteractiveEmptyEnvelopeJson));
    }

    [Fact]
    public void SerializeWithNoPolicy_OmitsTheMember()
    {
        // Not "policy":null — omitted, so an older reader sees an unchanged document byte for byte.
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
