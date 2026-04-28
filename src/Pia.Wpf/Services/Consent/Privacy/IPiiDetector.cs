namespace Pia.Services.Consent.Privacy;

public enum PiiType
{
    Name,
    Email,
    Iban,
    Phone,
    Address,
    CreditCard,
}

public sealed record PiiSpan(int Start, int Length, PiiType Type, string Value);

public interface IPiiDetector
{
    IReadOnlyList<PiiSpan> Detect(string text);
}
