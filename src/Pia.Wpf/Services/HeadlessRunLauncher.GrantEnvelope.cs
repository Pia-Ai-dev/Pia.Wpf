using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services;

public sealed partial class HeadlessRunLauncher
{
    /// <summary>Compared with <c>!=</c>, so bumping it makes every older envelope unreadable and sends those
    /// runs to the resume floor — which for an interactive envelope is WIDER than the launch. Additive members
    /// need no bump: the options below set no <c>UnmappedMemberHandling</c>.</summary>
    private const int GrantEnvelopeVersion = 1;

    /// <summary>What <c>SerializeGrantEnvelope([], AgentRunTrigger.User)</c> produces, pinned byte-for-byte
    /// against it by <c>HeadlessRunLauncherPolicyTests</c>. <c>ChatSessionManager</c> uses it when serialization
    /// faults, because <c>null</c> there would apply the resume floor (<c>{write_file}</c>) — wider than what an
    /// interactive launch granted.</summary>
    internal const string InteractiveEmptyEnvelopeJson = """{"v":1,"grantedWrites":[],"trigger":"User"}""";

    private static readonly JsonSerializerOptions GrantEnvelopeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serialize the launch's grants into the opaque <c>AgentRuns.PolicyJson</c> envelope. The run
    /// service stores the string verbatim and never parses it, so the shape stays private here.</summary>
    /// <param name="policy">Null OMITS the member entirely, so a policy-less document stays byte-identical to
    /// one written before policies existed.</param>
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
    /// The grant set a CHILD inherits: a strict subset of the parent's. Every delete-like name is stripped even
    /// when the parent held it — a delegate does the work it was asked for and does not get to destroy anything.
    /// An unreadable parent envelope yields the EMPTY set, never a default and never the resume floor, or a
    /// child that inherits nothing readable could end up wider than its parent. The policy passes through
    /// unchanged: it is a tool-CLASS set that can never cover a delete-like tool, so narrowing it further would
    /// only stop the child doing its work.
    /// </summary>
    /// <remarks>Filtering by NAME is legitimate here because this authors a grant list rather than gating a
    /// call; the execution gates are untouched and still the only boundary.</remarks>
    internal static (IReadOnlyList<string> Grants, IReadOnlyList<string> Denied, RunAutonomyPolicy? Policy) NarrowForChild(
        string? parentPolicyJson, ILogger? logger = null)
    {
        var inherited = TryRestoreGrantEnvelope(parentPolicyJson) ?? [];
        var grants = inherited.Where(g => !ToolPermissionService.IsDeleteLike(g)).ToList();

        // COUNT only: a grant name can be an MCP-adjacent string, which is not ours to write to a support log.
        if (grants.Count != inherited.Count)
            logger?.LogInformation("Child run grants dropped {DroppedCount} delete-like names the parent held", inherited.Count - grants.Count);

        // Denials pass through unfiltered: a denial is a narrowing, so keeping the parent's can never widen the
        // child, and dropping them would let a delegate re-ask what the parent's person already settled.
        return (grants, TryRestoreDeniedWritesEnvelope(parentPolicyJson), TryRestorePolicy(parentPolicyJson, logger));
    }

    /// <summary>The child's <c>PolicyJson</c>, through the existing serializer — additive members only, so the
    /// version is deliberately not bumped.</summary>
    /// <param name="trigger">The PARENT's trigger kind. Provenance only; never consulted to widen a grant, which
    /// is why misreporting a Schedule parent's child as User on the fault path below is acceptable.</param>
    /// <remarks>A fault falls back to the empty envelope and NOT to null: null would apply the resume floor,
    /// which can be wider than the parent.</remarks>
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
    /// Read the run's autonomy policy back. Null means TODAY'S BEHAVIOUR, not the grant floor — an unreadable
    /// envelope must lose the policy before it loses the grant list, since losing a policy is the restrictive
    /// direction and an unreadable grant list has to fall back to something the run can work with. Never throws.
    /// </summary>
    /// <remarks>Class names are validated as <see cref="ToolClass"/> members and nothing more, deliberately NOT
    /// intersected with the settings preset: that preset is not "everything an envelope may legally carry", and
    /// pinning the reader to it would silently narrow the first per-run policy someone authors.</remarks>
    internal static RunAutonomyPolicy? TryRestorePolicy(string? policyJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<GrantEnvelope>(policyJson, GrantEnvelopeJsonOptions);

            // The SAME readability test the grant reader applies, `GrantedWrites is null` included, so both
            // halves agree on what a readable envelope is and only the fallback differs. Without it,
            // `{"v":1,"policy":{…}}` handed back a full policy while the grant half fell back to the floor —
            // a resumed run auto-running by class with no named grant behind it.
            if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.GrantedWrites is null)
                return null;

            var names = envelope.Policy?.AutoApproveClasses;
            if (names is null || names.Count == 0)
                return null;

            var classes = new List<ToolClass>();
            var dropped = 0;
            foreach (var name in names)
            {
                // Unknown is dropped like any unparseable name: RunAutonomyPolicy.Covers hardcodes it to false,
                // so carrying it would only make the restored policy look wider than it is.
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

            // COUNT only, for the reason NarrowForChild logs a count.
            if (dropped > 0)
                logger?.LogInformation("Restored run policy dropped {DroppedCount} unrecognised class names", dropped);

            // No usable class ⇒ no policy, rather than an empty-but-present one.
            return classes.Count == 0 ? null : new RunAutonomyPolicy(classes);
        }
        catch (Exception)
        {
            // Garbage / foreign JSON is a "no policy" case, not an error case.
            return null;
        }
    }

    /// <summary>Read the grant list a launch persisted, so a resume restores exactly it and can never widen it.
    /// Null means "apply the resume floor"; a present-but-EMPTY list is honoured as an empty grant set, since a
    /// launch that granted no writes must not gain any on resume. Never throws.</summary>
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
            // Garbage / foreign JSON is a floor case, not an error case.
            return null;
        }
    }

    /// <summary>Read the run-scoped denial list a Decline persisted. Returns EMPTY on an unreadable envelope,
    /// never the grant floor's mirror: a denial is a narrowing, and losing it re-parks the tool (asks again)
    /// rather than running something ungranted. Never throws.</summary>
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

    /// <summary>The envelope persisted on <c>AgentRuns.PolicyJson</c>; camelCase on the wire.</summary>
    private sealed class GrantEnvelope
    {
        /// <summary>Absent/unknown → the reader applies the resume floor.</summary>
        public int V { get; set; }

        /// <summary>The write-tool names the LAUNCH resolved. A resume restores exactly this.</summary>
        public List<string>? GrantedWrites { get; set; }

        /// <summary>Origin trigger — diagnostics only; never consulted to widen a grant.</summary>
        public string? Trigger { get; set; }

        /// <summary><c>WhenWritingNull</c> is scoped to THIS member rather than the shared options object, so a
        /// policy-less document stays byte-identical to one written before policies existed.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PolicyDto? Policy { get; set; }

        /// <summary>Tools a person declined for this run. An absent member reads back as no denials, so a
        /// document written before denials existed is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? DeniedWrites { get; set; }
    }

    /// <summary>Class NAMES, not ordinals: a name an older build cannot parse is dropped (restrictive) instead
    /// of silently colliding with a member it does know.</summary>
    private sealed class PolicyDto
    {
        public List<string>? AutoApproveClasses { get; set; }
    }
}
