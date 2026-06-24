namespace Pia.Models;

public sealed record SourceRef(int Number, string Source, string Meta, string? Url = null);

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
