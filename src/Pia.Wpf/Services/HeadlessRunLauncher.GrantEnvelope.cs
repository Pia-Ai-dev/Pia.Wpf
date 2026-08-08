using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services;

public sealed partial class HeadlessRunLauncher
{
    /// <summary>
    /// Envelope shape currently written/understood by this launcher. Anything else → the floor.
    /// <para>
    /// Batch 04 added the <c>policy</c> member WITHOUT touching this (04 D1). The reader below compares with
    /// <c>!=</c>, so a bump would make every envelope written before that batch unreadable → the resume floor
    /// → and for an interactive-origin envelope (<c>grantedWrites: []</c>) the floor is WIDER than the launch,
    /// i.e. a silent escalation of every in-flight interactive run. <see cref="GrantEnvelopeJsonOptions"/>
    /// sets no <c>UnmappedMemberHandling</c>, so additive members interoperate in both directions for free.
    /// </para>
    /// </summary>
    private const int GrantEnvelopeVersion = 1;

    /// <summary>
    /// The exact document <c>SerializeGrantEnvelope([], AgentRunTrigger.User)</c> produces with no policy.
    /// Used by <c>ChatSessionManager</c> when serialization FAULTS: <c>null</c> there would make the resume
    /// fall back to <see cref="ResumeFloorGrants"/> (<c>{write_file}</c>), which is WIDER than what an
    /// interactive launch granted (nothing). Deliberately carries no <c>policy</c> member — a fault fallback
    /// grants nothing and auto-approves nothing, and narrower-on-fault is the only acceptable direction.
    /// Pinned byte-for-byte against the serializer by <c>HeadlessRunLauncherPolicyTests</c>.
    /// </summary>
    internal const string InteractiveEmptyEnvelopeJson = """{"v":1,"grantedWrites":[],"trigger":"User"}""";

    private static readonly JsonSerializerOptions GrantEnvelopeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serialize the grants a launch resolved into the opaque <c>AgentRuns.PolicyJson</c> envelope (D1).
    /// The run service stores the string verbatim and never parses it, so the shape stays private to this
    /// launcher; <c>v</c> lets a later shape change be detected instead of misread.
    /// </summary>
    /// <param name="policy">The run's autonomy policy, or null. Null OMITS the member entirely (not
    /// <c>"policy":null</c>), so a policy-less document is byte-identical to a pre-Batch-04 one.</param>
    /// <param name="deniedWrites">Tools declined for this run on a tool-approval park, or null (omitted).</param>
    internal static string SerializeGrantEnvelope(
        IReadOnlyCollection<string> grants, AgentRunTrigger trigger, RunAutonomyPolicy? policy = null,
        IReadOnlyCollection<string>? deniedWrites = null)
        => JsonSerializer.Serialize(
            new GrantEnvelope
            {
                V = GrantEnvelopeVersion,
                GrantedWrites = grants.ToList(),
                Trigger = trigger.ToString(),
                Policy = policy is null
                    ? null
                    : new PolicyDto { AutoApproveClasses = policy.AutoApproveClasses.Select(c => c.ToString()).ToList() },
                DeniedWrites = deniedWrites is null or { Count: 0 }
                    ? null
                    : deniedWrites.ToList(),
            },
            GrantEnvelopeJsonOptions);

    /// <summary>
    /// The grant set + policy a CHILD run inherits: a strict SUBSET of the parent's, never the default and
    /// never the resume floor. A child is a delegate — it does the work the parent asked for and it does not
    /// get to destroy anything, so every delete-like NAME is stripped even when the parent held it (the parent
    /// can still delete, in its own steps).
    /// <para>
    /// An UNREADABLE parent envelope yields the EMPTY grant set, NOT
    /// <c>HeadlessRunRequest.DefaultGrantedWrites</c> and NOT <see cref="ResumeFloorGrants"/>: falling through
    /// to a default would let a child that inherits nothing readable end up WIDER than its parent, which is the
    /// one thing this helper exists to make impossible (Phase 3 R13). "Readable" means exactly what it means at
    /// resume, because this is the same reader — <see cref="TryRestoreGrantEnvelope"/>.
    /// </para>
    /// <para>
    /// The policy passes through UNCHANGED. It is a tool-CLASS set that can never cover a delete-like tool
    /// (04 D6 — the floor in <c>ToolAutonomy.Resolve</c> is evaluated before any policy branch), so narrowing it
    /// further would only make a child unable to do the work it was delegated, and it is ⊆ the parent's by
    /// construction. Pinned by <c>HeadlessRunLauncherChildRunTests</c>.
    /// </para>
    /// <para>
    /// Name filtering is legitimate HERE: this file is not one of <c>ToolAutonomyRuleTests.GateFiles</c> — it
    /// AUTHORS a grant list rather than gating a call, exactly like <c>ScheduledJobToolHandler.ParseGrantedTools</c>
    /// does at create time. The execution gates are untouched and still the only boundary.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<string> Grants, IReadOnlyList<string> Denied, RunAutonomyPolicy? Policy) NarrowForChild(
        string? parentPolicyJson, ILogger? logger = null)
    {
        var inherited = TryRestoreGrantEnvelope(parentPolicyJson) ?? [];
        var grants = inherited.Where(g => !ToolPermissionService.IsDeleteLike(g)).ToList();

        // COUNT only. A grant name can be an MCP-adjacent string, which is not ours to write to a support log —
        // the same rule TryRestorePolicy's dropped-class count follows.
        if (grants.Count != inherited.Count)
            logger?.LogInformation("Child run grants dropped {DroppedCount} delete-like names the parent held", inherited.Count - grants.Count);

        // Denials pass through UNFILTERED: a denial is a narrowing, so a child keeping the parent's declines
        // can never widen it, and dropping them would let a delegate re-ask what the parent's person settled.
        return (grants, TryRestoreDeniedWritesEnvelope(parentPolicyJson), TryRestorePolicy(parentPolicyJson, logger));
    }

    /// <summary>
    /// The child's <c>PolicyJson</c>: <see cref="NarrowForChild"/>'s result through the EXISTING <c>v:1</c>
    /// serializer. The envelope version is deliberately NOT bumped — additive members only, because
    /// <see cref="GrantEnvelopeVersion"/> is compared with <c>!=</c> (see its remarks).
    /// </summary>
    /// <param name="trigger">The PARENT's trigger kind. Provenance only — "diagnostics only; never consulted to
    /// widen a grant", as <see cref="GrantEnvelope.Trigger"/> says.</param>
    /// <remarks>
    /// Unlike <see cref="TrySerializeGrantEnvelope"/>, a serializer fault here falls back to
    /// <see cref="InteractiveEmptyEnvelopeJson"/> and NOT to <c>null</c>: null would make the child's resume
    /// apply <see cref="ResumeFloorGrants"/> (<c>{write_file}</c>), which can be WIDER than the parent — the
    /// identical argument that constant already exists for. Its <c>"trigger":"User"</c> then misreports a
    /// Schedule-parent's child, which is acceptable precisely because trigger never widens anything. That arm is
    /// a GUARD, not a fixed defect: serializing a <c>List&lt;string&gt;</c> plus a class-name list cannot
    /// realistically fault, so it is unreachable in practice and is not covered by a red-before-green demo.
    /// </remarks>
    internal static string TrySerializeChildEnvelope(
        string? parentPolicyJson, AgentRunTrigger trigger, ILogger? logger = null)
    {
        var (grants, denied, policy) = NarrowForChild(parentPolicyJson, logger);
        try
        {
            return SerializeGrantEnvelope(grants, trigger, policy, denied);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to serialize a child run's grant envelope; granting the child nothing");
            return InteractiveEmptyEnvelopeJson;
        }
    }

    /// <summary>
    /// Read the run's autonomy policy back out of the envelope (04 D10). Returns <c>null</c> — meaning
    /// "TODAY'S BEHAVIOUR", NOT the grant floor — for an absent/unreadable envelope, an absent <c>policy</c>
    /// member, or a member whose class names this build does not recognise. Never throws.
    /// <para>
    /// The asymmetry against <see cref="TryRestoreGrantEnvelope"/> is the whole backward-compatibility
    /// guarantee: an unreadable envelope loses the POLICY before it loses the grant list, and losing the
    /// policy is always the restrictive direction. An unreadable grant list has to fall back to something the
    /// run can work with; an unreadable policy falls back to nothing. The two readers therefore apply the same
    /// readability test — version AND a present <c>grantedWrites</c> — so "readable" cannot mean one thing here
    /// and another there; only the FALLBACK differs.
    /// </para>
    /// <para>
    /// Class names are validated as <see cref="ToolClass"/> members and nothing more. They are deliberately NOT
    /// intersected with <c>RunAutonomyPolicy.PresetClasses</c>: that list is the SETTINGS preset, not "everything
    /// an envelope may legally carry", so pinning the reader to it would silently narrow the first per-run policy
    /// a later batch authors, with no failing test to explain why. §13.2's filtering belongs at the point a
    /// policy is AUTHORED from untrusted input, which is a different chokepoint from this resume reader.
    /// </para>
    /// <para>
    /// A resume calls this and NEVER <c>RunAutonomyPolicy.FromSettings</c>: the envelope is the run's
    /// authority of record, so flipping the setting between park and Continue cannot widen a parked run.
    /// Unrecognised class names are dropped and only their COUNT is logged — an MCP-adjacent string is not
    /// ours to write to a support log.
    /// </para>
    /// </summary>
    internal static RunAutonomyPolicy? TryRestorePolicy(string? policyJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<GrantEnvelope>(policyJson, GrantEnvelopeJsonOptions);

            // The SAME readability test TryRestoreGrantEnvelope applies, `GrantedWrites is null` included, so
            // both halves of the reader agree on what "a readable envelope" means. Without it the documented
            // asymmetry INVERTS for one document shape: `{"v":1,"policy":{…}}` with no grantedWrites made the
            // grant half fall back to the {write_file} floor as if the envelope were unreadable while this half
            // handed back a full policy — a resumed run auto-running by class with no named grant behind it.
            if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.GrantedWrites is null)
                return null;

            var names = envelope.Policy?.AutoApproveClasses;
            if (names is null || names.Count == 0)
                return null;

            var classes = new List<ToolClass>();
            var dropped = 0;
            foreach (var name in names)
            {
                // OrdinalIgnoreCase against the enum member names. Unknown is dropped like any unparseable
                // name: RunAutonomyPolicy.Covers hardcodes it to false anyway, so carrying it would only make
                // the restored policy look wider than it is.
                if (!string.IsNullOrWhiteSpace(name)
                    && Enum.TryParse<ToolClass>(name.Trim(), ignoreCase: true, out var parsed)
                    && parsed != ToolClass.Unknown)
                {
                    if (!classes.Contains(parsed))
                        classes.Add(parsed);
                }
                else
                {
                    dropped++;
                }
            }

            if (dropped > 0)
                logger?.LogInformation("Restored run policy dropped {DroppedCount} unrecognised class names", dropped);

            // No usable class ⇒ no policy, which is today's behaviour rather than an empty-but-present one.
            return classes.Count == 0 ? null : new RunAutonomyPolicy(classes);
        }
        catch (Exception)
        {
            // Garbage / foreign JSON is a "no policy" case, not an error case.
            return null;
        }
    }

    /// <summary>
    /// Read the grant list a launch persisted, so a resume restores exactly what the launch granted and
    /// can never widen it (D1). Returns <c>null</c> — meaning "apply the resume FLOOR" — when the envelope
    /// is absent, unparseable, of an unknown version, or carries no <c>grantedWrites</c> member at all.
    /// A present-but-EMPTY list is honoured as an empty grant set (a launch that granted no writes must
    /// not gain any on resume). Never throws.
    /// </summary>
    internal static IReadOnlyList<string>? TryRestoreGrantEnvelope(string? policyJson)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<GrantEnvelope>(policyJson, GrantEnvelopeJsonOptions);
            if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.GrantedWrites is null)
                return null;

            return envelope.GrantedWrites
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // Garbage / foreign JSON in PolicyJson is a floor case, not an error case.
            return null;
        }
    }

    /// <summary>
    /// Read the run-scoped denial list a Decline persisted into the envelope. Returns EMPTY — never the grant
    /// floor's mirror — for an absent/unreadable envelope or an absent member: a denial is a narrowing, and
    /// losing it on a corrupt envelope re-parks the tool (asks again) instead of running something ungranted.
    /// Never throws.
    /// </summary>
    internal static IReadOnlyList<string> TryRestoreDeniedWritesEnvelope(string? policyJson)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return [];

        try
        {
            var envelope = JsonSerializer.Deserialize<GrantEnvelope>(policyJson, GrantEnvelopeJsonOptions);
            if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.DeniedWrites is null)
                return [];

            return envelope.DeniedWrites
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The launch-grant envelope persisted on <c>AgentRuns.PolicyJson</c>. Private to this file
    /// (see <see cref="SerializeGrantEnvelope"/>); camelCase on the wire like the rest of this codebase.
    /// </summary>
    private sealed class GrantEnvelope
    {
        /// <summary>Envelope version. Absent/unknown → the reader applies the resume floor.</summary>
        public int V { get; set; }

        /// <summary>The write-tool names the LAUNCH resolved. A resume restores exactly this.</summary>
        public List<string>? GrantedWrites { get; set; }

        /// <summary>Origin trigger — diagnostics only; never consulted to widen a grant.</summary>
        public string? Trigger { get; set; }

        /// <summary>
        /// Batch 04 autonomy policy. ADDITIVE at <c>v:1</c> — <see cref="GrantEnvelopeVersion"/> is
        /// deliberately NOT bumped (see its remarks). <c>WhenWritingNull</c> is scoped to THIS member, not to
        /// the shared options object, so a policy-less document stays byte-identical to a pre-04 one and
        /// nothing has to be argued about <c>V</c> / <c>GrantedWrites</c> / <c>Trigger</c>.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PolicyDto? Policy { get; set; }

        /// <summary>
        /// Tools a person DECLINED for this run on a tool-approval park. ADDITIVE at <c>v:1</c> like
        /// <see cref="Policy"/> — an absent member reads back as no denials, so a pre-denial document is
        /// unchanged. A denial is a NARROWING, so unlike the grant list an unreadable envelope restores none.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? DeniedWrites { get; set; }
    }

    /// <summary>
    /// Wire shape of the autonomy policy. Class NAMES, not ordinals: a name an older build cannot parse is
    /// DROPPED (restrictive) instead of silently colliding with a member it does know.
    /// </summary>
    private sealed class PolicyDto
    {
        public List<string>? AutoApproveClasses { get; set; }
    }
}
