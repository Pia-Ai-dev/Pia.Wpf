using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// The 04 ↔ 03 shared vocabulary, mechanized. Batch 04 defined <see cref="ToolClass"/>,
/// <see cref="ToolGateSurface"/> and <see cref="ToolGateDecision"/>; Batch 03 persists all three as ordinals
/// and adds two of its own. These facts exist so an append to 04's enums cannot silently leave 03 with a
/// decision it does not record and does not render.
/// </summary>
public class AgentTimelineVocabularyTests
{
    /// <summary>
    /// Every decision the two RUN gates can reach, and which of Batch 03's emit arms writes it. Derived by
    /// walking the arms, not by copying the enum.
    /// </summary>
    private static readonly ToolGateDecision[] EmittedByAGate =
    [
        ToolGateDecision.AutoApprovedStandingGrant, // interactive AutoRun (verdict.Decision)
        ToolGateDecision.AutoApprovedPolicy,        // either surface, AutoRun (verdict.Decision)
        ToolGateDecision.GrantedByName,             // unattended AutoRun (verdict.Decision)
        ToolGateDecision.ApprovedOnce,              // interactive card, AllowOnce
        ToolGateDecision.ApprovedAlways,            // interactive card, AlwaysAllow
        ToolGateDecision.DeclinedByUser,            // interactive card, declined
        ToolGateDecision.CardCancelled,             // interactive card, cancelled (NOT a user denial)
        ToolGateDecision.DeniedNotGranted,          // unattended default
        ToolGateDecision.DeniedDestructiveFloor,    // unattended floor refusal
        ToolGateDecision.UnknownTool,               // either surface, null route
        ToolGateDecision.ParkedForApproval,         // hermes #16: unattended Park (the FIRST parked call only)
        // hermes #15, the session tier — BOTH surfaces reach the first one (interactive AutoRun and, since the
        // tier is armed on the same condition as the park, a root run's unattended AutoRun), while the second
        // is written only where the grant is minted: the interactive card.
        ToolGateDecision.AutoApprovedSessionGrant,  // either surface, AutoRun (verdict.Decision)
        ToolGateDecision.ApprovedForSession,        // interactive card, AllowForSession
        // The deny beside a tool-approval park: the unattended gate's DeniedForRun Refuse arm writes it on
        // the re-run call a declined resume refuses.
        ToolGateDecision.DeniedForRun,
    ];

    /// <summary>
    /// Decisions a gate can produce that this batch deliberately does NOT record, each with its reason.
    /// A new entry here is a decision, not an omission.
    /// </summary>
    private static readonly ToolGateDecision[] NotEmittedByDesign =
    [
        // Voice-mode only (04 D13). A voice turn has no run, so there is no RunId to attach a row to and the
        // enforced FK would reject one. The value exists so a later batch that gives voice turns a run needs
        // no new ordinal.
        ToolGateDecision.AutoApprovedAllowlist,
    ];

    [Fact]
    public void EveryToolGateDecision_IsEitherEmittedOrDocumentedAsNotEmitted()
    {
        var unaccounted = Enum.GetValues<ToolGateDecision>()
            .Where(d => d != ToolGateDecision.Unknown)
            .Where(d => !EmittedByAGate.Contains(d) && !NotEmittedByDesign.Contains(d))
            .ToArray();

        // Adding a decision to Batch 04 without deciding what Batch 03 does with it fails HERE.
        Assert.Empty(unaccounted);

        // The two sets are disjoint and neither claims Unknown (which is a render value, never written).
        Assert.Empty(EmittedByAGate.Intersect(NotEmittedByDesign));
        Assert.DoesNotContain(ToolGateDecision.Unknown, EmittedByAGate);
        Assert.DoesNotContain(ToolGateDecision.Unknown, NotEmittedByDesign);
    }

    /// <summary>
    /// The golden ordinal map for every enum this batch PERSISTS — the append-only guardrail, mechanized.
    /// <para>
    /// The shape assertions below (min 0, Unknown at 0, distinct, contiguous) do NOT catch the failure they
    /// exist to prevent: alphabetizing <c>ToolGateDecision</c> or inserting a member keeps all four true while
    /// swapping <c>ApprovedOnce</c> and <c>ApprovedAlways</c>, so every already-persisted row would read back
    /// as a decision the user never made — and these ordinals also travel in <c>AgentRuns.PolicyJson</c>, where
    /// an older peer's stored value gets reinterpreted. A name→ordinal map is the only assertion that turns a
    /// rename, a renumber and a removal all red, with a diff naming the member.
    /// </para>
    /// <para>
    /// APPENDING to one of these enums is legal and requires adding the new member HERE with its ordinal.
    /// Changing an existing pair is not.
    /// </para>
    /// </summary>
    [Fact]
    public void PersistedEnumOrdinalsMatchTheGoldenMap()
    {
        AssertGoldenMap(new Dictionary<string, int>
        {
            ["Unknown"] = 0,
            ["ToolCall"] = 1,
            ["TraceTruncated"] = 2,
        }, Enum.GetValues<AgentTimelineEventKind>());

        AssertGoldenMap(new Dictionary<string, int>
        {
            ["Unknown"] = 0,
            ["Ok"] = 1,
            ["Error"] = 2,
            ["NotExecuted"] = 3,
        }, Enum.GetValues<AgentTimelineOutcome>());

        AssertGoldenMap(new Dictionary<string, int>
        {
            ["Unknown"] = 0,
            ["Memory"] = 1,
            ["Todo"] = 2,
            ["Reminder"] = 3,
            ["Files"] = 4,
            ["Git"] = 5,
            ["Scheduling"] = 6,
            ["External"] = 7,
            ["Ingest"] = 8,
        }, Enum.GetValues<ToolClass>());

        AssertGoldenMap(new Dictionary<string, int>
        {
            ["Unknown"] = 0,
            ["Interactive"] = 1,
            ["Unattended"] = 2,
            ["Voice"] = 3,
        }, Enum.GetValues<ToolGateSurface>());

        AssertGoldenMap(new Dictionary<string, int>
        {
            ["Unknown"] = 0,
            ["AutoApprovedStandingGrant"] = 1,
            ["AutoApprovedPolicy"] = 2,
            ["GrantedByName"] = 3,
            ["ApprovedOnce"] = 4,
            ["ApprovedAlways"] = 5,
            ["DeclinedByUser"] = 6,
            ["CardCancelled"] = 7,
            ["DeniedNotGranted"] = 8,
            ["DeniedDestructiveFloor"] = 9,
            ["UnknownTool"] = 10,
            ["AutoApprovedAllowlist"] = 11,
            ["ParkedForApproval"] = 12,
            ["AutoApprovedSessionGrant"] = 13,
            ["ApprovedForSession"] = 14,
            ["DeniedForRun"] = 15,
        }, Enum.GetValues<ToolGateDecision>());
    }

    private static void AssertGoldenMap<TEnum>(Dictionary<string, int> expected, TEnum[] values)
        where TEnum : struct, Enum
    {
        var actual = values.ToDictionary(v => Enum.GetName(v)!, v => Convert.ToInt32(v));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PersistedTimelineEnumsStartAtUnknownZero_AndNeverCollide()
    {
        AssertAppendOnlyShape<AgentTimelineEventKind>();
        AssertAppendOnlyShape<AgentTimelineOutcome>();

        // 04's three are persisted by this batch too, so their shape is 03's problem as well.
        AssertAppendOnlyShape<ToolClass>();
        AssertAppendOnlyShape<ToolGateSurface>();
        AssertAppendOnlyShape<ToolGateDecision>();
    }

    private static void AssertAppendOnlyShape<TEnum>() where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        var ordinals = values.Select(v => Convert.ToInt32(v)).ToArray();

        Assert.Equal(0, ordinals.Min());
        Assert.Equal("Unknown", Enum.GetName(values.First(v => Convert.ToInt32(v) == 0))!);
        // A duplicate ordinal means two names share a persisted value: the read side can no longer tell them
        // apart, which is the same defect as a renumber.
        Assert.Equal(ordinals.Length, ordinals.Distinct().Count());
        // Contiguous from 0, so "all ordinals 0..N" in a render test really is exhaustive.
        Assert.Equal(Enumerable.Range(0, ordinals.Length), ordinals.OrderBy(o => o));
    }
}
