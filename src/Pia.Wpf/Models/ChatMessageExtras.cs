using System.IO;

namespace Pia.Models;

public sealed record SourceRef(int Number, string Source, string Meta, string? Url = null);

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

public sealed record AnswerStats(int Tokens, string Model)
{
    public string Summary => $"{Tokens:N0} Tokens · {Model}";
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
