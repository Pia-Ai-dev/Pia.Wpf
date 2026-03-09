namespace Pia.Services.Interfaces;

public record PiiMatch(string Value, string Category, int Start, int Length);

public interface IPiiDetector
{
    IReadOnlyList<PiiMatch> DetectPii(string text);
    IReadOnlyList<PiiMatch> DetectPiiInStructured(string json, string memoryType);
}
