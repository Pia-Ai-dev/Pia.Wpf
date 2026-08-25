namespace Pia.Services.Exceptions;

/// <summary>
/// Thrown only where the launcher can PROVE nothing was spent and nothing written — before the stub chat is
/// saved. It is the caller vouching for the verdict, which is what widening the pre-model retry needs; a
/// generic exception type could never carry that.
/// </summary>
public sealed class PreModelLaunchException : InvalidOperationException
{
    public PreModelLaunchException(string message) : base(message)
    {
    }
}
