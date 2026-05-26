namespace Pia.Models;

public sealed record SourceRef(int Number, string Source, string Meta, string? Url = null);

public sealed record MessageMeta(string Timing, string? ProfileLabel = null);

public sealed record AnswerStats(int Tokens, string Model)
{
    public string Summary => $"{Tokens:N0} Tokens · {Model}";
}
