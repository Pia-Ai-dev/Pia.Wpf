namespace Pia.Services;

/// <summary>The embedding model cannot run in this process; callers drop the vector tier instead of failing.</summary>
public sealed class EmbeddingUnavailableException : InvalidOperationException
{
    public EmbeddingUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
