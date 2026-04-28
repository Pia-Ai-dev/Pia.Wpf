namespace Pia.Services.Consent.Privacy;

/// <summary>
/// Bidirectional mapping between original PII tokens and their placeholders. Session-scoped:
/// never persisted, never crosses meetings. The same original value receives the same
/// placeholder for the lifetime of the map so cloud responses can be reverse-mapped
/// deterministically.
/// </summary>
public sealed class PseudonymisationMap
{
    private readonly Dictionary<string, string> _toPseudonym = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toOriginal = new(StringComparer.Ordinal);
    private readonly Dictionary<PiiType, int> _counters = new();

    public int Count => _toPseudonym.Count;

    public string GetOrAssign(PiiType type, string original)
    {
        if (_toPseudonym.TryGetValue(original, out var existing)) return existing;

        _counters.TryGetValue(type, out var n);
        n++;
        _counters[type] = n;
        var placeholder = $"[{type.ToString().ToUpperInvariant()}-{n}]";
        _toPseudonym[original] = placeholder;
        _toOriginal[placeholder] = original;
        return placeholder;
    }

    public bool TryGetOriginal(string placeholder, out string original)
        => _toOriginal.TryGetValue(placeholder, out original!);

    public IReadOnlyDictionary<string, string> Placeholders => _toOriginal;
}
