namespace Pia.Services.Interfaces;

public interface ITokenMapService
{
    string Tokenize(string value, string category);
    string TokenizeStructuredResult(string formattedResult);
    string Detokenize(string text);

    /// <summary>
    /// Like <see cref="Detokenize"/> but tolerant of tokens whose bracket form was mangled by a
    /// generative rewrite (lowercased category, or a <c>-</c>/space separator instead of <c>_</c>,
    /// e.g. <c>[person-1]</c>): matches <c>[category(sep)number]</c> case-insensitively, normalizes it
    /// back to the canonical <c>[Category_number]</c> form and restores it. Unknown/unmatched brackets
    /// pass through unchanged. Used before persisting synthesized ingest pages, where the model rewrites
    /// content into prose and may not echo placeholder tokens verbatim.
    /// </summary>
    string DetokenizeLoose(string text);

    /// <summary>
    /// Reverses the BRACKET-LESS token shape a generative rewrite sometimes leaves in a short title
    /// or subject (e.g. <c>Person_1</c> instead of <c>[Person_1]</c>), which neither
    /// <see cref="Detokenize"/> nor <see cref="DetokenizeLoose"/> match (both require brackets). Only
    /// known tokens are restored — unknown bare shapes pass through. Word-bounded and
    /// longest-token-first so <c>Person_1</c> cannot partially match inside <c>Person_11</c>. Used
    /// before persisting an ingest topic subject as a slug/title, where the extractor model may drop
    /// the brackets around a placeholder.
    /// </summary>
    string DetokenizeBare(string text);
    string? GetToken(string value, string category);
    Task InitializeAsync();
    void Clear();
}
