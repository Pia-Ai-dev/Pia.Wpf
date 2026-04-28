using System.Text;
using System.Text.RegularExpressions;

namespace Pia.Services.Consent.Privacy;

public sealed class Pseudonymiser
{
    private static readonly Regex PlaceholderRx = new(
        @"\[(NAME|EMAIL|IBAN|PHONE|ADDRESS|CREDITCARD)-\d+\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IPiiDetector _detector;

    public Pseudonymiser(IPiiDetector detector)
    {
        _detector = detector;
    }

    public string Apply(string text, PseudonymisationMap map)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var spans = _detector.Detect(text);
        if (spans.Count == 0) return text;

        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var s in spans)
        {
            if (s.Start > cursor) sb.Append(text, cursor, s.Start - cursor);
            sb.Append(map.GetOrAssign(s.Type, s.Value));
            cursor = s.Start + s.Length;
        }
        if (cursor < text.Length) sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    public string Reverse(string text, PseudonymisationMap map)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return PlaceholderRx.Replace(text, m =>
            map.TryGetOriginal(m.Value, out var original) ? original : m.Value);
    }
}
