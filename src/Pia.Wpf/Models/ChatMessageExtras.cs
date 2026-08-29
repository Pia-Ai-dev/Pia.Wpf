using System.Globalization;
using System.IO;

namespace Pia.Models;

/// <summary>What a source chip points at — decides its badge and what a click opens.</summary>
public enum SourceRefKind
{
    /// <summary>A web page, tied to a numbered <c>[N]</c> marker in the answer text.</summary>
    Web,

    /// <summary>A vault page, addressed by its wikilink target (no <c>memory/</c> prefix).</summary>
    VaultPage,

    /// <summary>A past conversation, addressed by its chat id.</summary>
    Chat,
}

/// <summary>
/// One thing the answer drew on. <c>Number</c> is meaningful only for <see cref="SourceRefKind.Web"/>,
/// where it matches the marker in the text; the other kinds carry 0 and render a kind glyph instead.
/// </summary>
public sealed record SourceRef(
    int Number,
    string Source,
    string Meta,
    string? Url = null,
    SourceRefKind Kind = SourceRefKind.Web,
    string? Target = null,
    int Ordinal = 0)
{
    /// <summary>
    /// AutomationId suffix. Not <c>Number</c> (0 on every unnumbered kind) and not <c>Target</c>, which would
    /// put a user-named vault page into a permanent, enumerable UIA property — see PiaFileChip's same rule.
    /// </summary>
    public string ChipId => Ordinal.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A local file an assistant turn touched — read, created/updated, exported, or referenced via
/// <c>@File</c>. Surfaced under the answer as an "open file / open folder" chip (see PiaFileChip),
/// the local-file analogue of <see cref="SourceRef"/>. In-memory only (not persisted, like Sources).
/// </summary>
public sealed record FileRef(string AbsolutePath, FileRefKind Kind)
{
    public string FileName => Path.GetFileName(AbsolutePath);
}

/// <summary>
/// How a chat touched a file, ordered by precedence (highest wins when the same path is touched
/// more than once in a turn — see <c>AssistantMessage.AddOrUpgradeFileRef</c>).
/// </summary>
public enum FileRefKind
{
    Read,
    Referenced,
    Updated,
    Created,
    Exported,
}

/// <summary>
/// A model-offered "switch to Agent mode" chip (R8). Distinct from the string-based
/// <see cref="AssistantMessage.Suggestions"/> (whose click merely pastes text): clicking this
/// re-dispatches <see cref="Goal"/> as a Planned run. <see cref="Reason"/> is model content — never logged.
/// </summary>
public sealed record AgentModeSuggestion(string Goal, string Reason);

public sealed record MessageMeta(string Timing, string? ProfileLabel = null);

/// <param name="Tokens">Null when the provider sent no usage — the model is still shown then.</param>
/// <param name="Provider">Null for Pia Cloud, which picks the upstream model itself and is named as the service.</param>
public sealed record AnswerStats(int? Tokens, string Model, string? Provider = null)
{
    public string ProvenanceLabel => string.IsNullOrWhiteSpace(Provider) ? Model : $"{Provider} · {Model}";

    public bool IsPiaCloud => Provider is null && Model == AnswerProvenance.PiaCloudLabel;
}

/// <summary>
/// Immutable snapshot of the persona that produced an assistant message, taken at send time
/// so renames/deletes of the live persona never change historical attribution.
/// </summary>
public sealed record PersonaAttribution(Guid Id, string Name, string? Emoji)
{
    public static PersonaAttribution From(Persona persona) =>
        new(persona.Id, persona.Name, persona.Emoji);
}
