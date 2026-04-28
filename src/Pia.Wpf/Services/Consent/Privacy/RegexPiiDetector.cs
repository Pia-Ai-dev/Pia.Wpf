using System.Text.RegularExpressions;

namespace Pia.Services.Consent.Privacy;

/// <summary>
/// Pattern-based PII detector. Heuristic only — false positives are tolerable; false
/// negatives are the actual risk. Patterns are intentionally permissive on the side of
/// over-redaction. Order matters: earlier patterns win where spans overlap.
/// </summary>
public sealed class RegexPiiDetector : IPiiDetector
{
    private static readonly Regex EmailRx = new(
        @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // IBAN: 2 letters + 2 digits + 11..30 alphanumerics.
    private static readonly Regex IbanRx = new(
        @"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // DE phone numbers + international "+" prefix. Matches things like
    // +49 30 1234567, 0049-30-1234567, 030/1234567, 0151 12345678.
    private static readonly Regex PhoneRx = new(
        @"(?:\+|00)\d{1,3}[\s\-/]?\d[\d\s\-/]{6,}\d|\b0\d[\d\s\-/]{6,}\d",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // DE address heuristic: <Street> <Number><optional letter>
    private static readonly Regex AddressRx = new(
        @"\b[A-ZÄÖÜ][a-zäöüß]+(?:straße|strasse|str\.|weg|allee|platz|gasse|ring|damm)\s+\d{1,4}[a-z]?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 13–19 digit groups for credit cards (validated below with Luhn).
    private static readonly Regex CardRx = new(
        @"\b(?:\d[ \-]?){12,18}\d\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Two consecutive capitalised tokens — classic German first-name/last-name heuristic.
    // Will over-fire on sentence starts; downstream pseudonymiser is reversible so this
    // is acceptable. Tweak: require at least 3 chars per token.
    private static readonly Regex NameRx = new(
        @"\b[A-ZÄÖÜ][a-zäöüß]{2,}\s[A-ZÄÖÜ][a-zäöüß]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<PiiSpan> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<PiiSpan>();

        var spans = new List<PiiSpan>();
        AddMatches(text, EmailRx, PiiType.Email, spans);
        AddMatches(text, IbanRx, PiiType.Iban, spans);
        AddCardMatches(text, spans);
        AddMatches(text, PhoneRx, PiiType.Phone, spans);
        AddMatches(text, AddressRx, PiiType.Address, spans);
        AddMatches(text, NameRx, PiiType.Name, spans);

        // Resolve overlaps: earlier-added wins; later spans overlapping an existing span are dropped.
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new List<PiiSpan>(spans.Count);
        var lastEnd = -1;
        foreach (var s in spans)
        {
            if (s.Start < lastEnd) continue;
            result.Add(s);
            lastEnd = s.Start + s.Length;
        }
        return result;
    }

    private static void AddMatches(string text, Regex rx, PiiType type, List<PiiSpan> spans)
    {
        foreach (Match m in rx.Matches(text))
        {
            spans.Add(new PiiSpan(m.Index, m.Length, type, m.Value));
        }
    }

    private static void AddCardMatches(string text, List<PiiSpan> spans)
    {
        foreach (Match m in CardRx.Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is < 13 or > 19) continue;
            if (!Luhn(digits)) continue;
            spans.Add(new PiiSpan(m.Index, m.Length, PiiType.CreditCard, m.Value));
        }
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }
}
