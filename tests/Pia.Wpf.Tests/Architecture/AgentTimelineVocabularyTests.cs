using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// The gate/timeline shared vocabulary, mechanized: an append to a gate enum must not silently leave the
/// timeline with a decision it neither records nor renders.
/// </summary>
public class AgentTimelineVocabularyTests
{
    // Derived by walking the emit arms, not by copying the enum.
    private static readonly ToolGateDecision[] EmittedByAGate =
    [
        ToolGateDecision.AutoApprovedStandingGrant, // either surface, AutoRun (verdict.Decision)
        ToolGateDecision.AutoApprovedPolicy,        // either surface, AutoRun (verdict.Decision)
        ToolGateDecision.GrantedByName,             // unattended AutoRun (verdict.Decision)
        ToolGateDecision.ApprovedOnce,              // interactive card, AllowOnce
        ToolGateDecision.ApprovedAlways,            // interactive card, AlwaysAllow
        ToolGateDecision.DeclinedByUser,            // interactive card, declined
        ToolGateDecision.CardCancelled,             // interactive card, cancelled (NOT a user denial)
        ToolGateDecision.DeniedNotGranted,          // unattended default
        ToolGateDecision.UnknownTool,               // either surface, null route
        ToolGateDecision.ParkedForApproval,         // unattended Park (the FIRST parked call only)
        // The session tier: BOTH surfaces reach the first one, because the tier is armed on the same condition
        // as the park, while the second is written only where the grant is minted — the interactive card.
        ToolGateDecision.AutoApprovedSessionGrant,  // either surface, AutoRun (verdict.Decision)
        ToolGateDecision.ApprovedForSession,        // interactive card, AllowForSession
        // The deny beside a tool-approval park: the unattended gate's DeniedForRun Refuse arm writes it on
        // the re-run call a declined resume refuses.
        ToolGateDecision.DeniedForRun,
    ];

    // Decisions a gate can produce that the timeline deliberately does NOT record; an entry is a decision, not
    // an omission.
    private static readonly ToolGateDecision[] NotEmittedByDesign =
    [
        // Voice-mode only: a voice turn has no run, so there is no RunId to attach a row to and the enforced FK
        // would reject one.
        ToolGateDecision.AutoApprovedAllowlist,
    ];

    // Historic audit vocabulary: no arm of Resolve produces these any more, but stored rows carry them and the
    // renderer still maps them, so the ordinals cannot move.
    private static readonly ToolGateDecision[] LegacyAuditVocabulary =
    [
        ToolGateDecision.DeniedDestructiveFloor,
    ];

    [Fact]
    public void EveryToolGateDecision_IsEitherEmittedOrDocumentedAsNotEmitted()
    {
        var unaccounted = Enum.GetValues<ToolGateDecision>()
            .Where(d => d != ToolGateDecision.Unknown)
            .Where(d => !EmittedByAGate.Contains(d)
                        && !NotEmittedByDesign.Contains(d)
                        && !LegacyAuditVocabulary.Contains(d))
            .ToArray();

        // Adding a gate decision without deciding what the timeline does with it fails HERE.
        Assert.Empty(unaccounted);

        // The three sets are disjoint and none claims Unknown (which is a render value, never written).
        Assert.Empty(EmittedByAGate.Intersect(NotEmittedByDesign));
        Assert.Empty(EmittedByAGate.Intersect(LegacyAuditVocabulary));
        Assert.Empty(NotEmittedByDesign.Intersect(LegacyAuditVocabulary));
        Assert.DoesNotContain(ToolGateDecision.Unknown, EmittedByAGate);
        Assert.DoesNotContain(ToolGateDecision.Unknown, NotEmittedByDesign);
        Assert.DoesNotContain(ToolGateDecision.Unknown, LegacyAuditVocabulary);
    }

    // The shape assertions further down stay true under a reorder that swaps two persisted ordinals, so only a
    // name→ordinal map reds for a rename, a renumber or a removal. Appending means adding the new pair here.
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

        // The gate's three are persisted by the timeline too, so their shape is checked here as well.
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
