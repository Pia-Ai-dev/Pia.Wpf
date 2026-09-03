namespace Pia.Services.Exceptions;

/// <summary>
/// Pia Cloud turned the optimize payload down for its length. The cap is a server setting, so
/// <see cref="LimitCharacters"/> is null against a server that does not report it.
/// </summary>
public sealed class OptimizeTextTooLongException : Exception
{
    public int? LimitCharacters { get; }
    public int TextLength { get; }

    public OptimizeTextTooLongException(int textLength, int? limitCharacters)
        : base($"The optimize payload is {textLength} characters, past the server's limit.")
    {
        TextLength = textLength;
        LimitCharacters = limitCharacters;
    }
}
