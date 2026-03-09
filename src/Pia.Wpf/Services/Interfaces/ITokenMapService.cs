namespace Pia.Services.Interfaces;

public interface ITokenMapService
{
    string Tokenize(string value, string category);
    string TokenizeStructuredResult(string formattedResult);
    string Detokenize(string text);
    string? GetToken(string value, string category);
    Task InitializeAsync();
    void Clear();
}
