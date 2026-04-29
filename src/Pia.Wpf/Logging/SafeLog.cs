using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Pia.Logging;

// Sensitive-payload log methods that are erased from RELEASE builds.
//
// [Conditional("DEBUG")] causes the C# compiler to strip every call to these
// methods — and the evaluation of all their arguments — out of the release IL.
// That guarantees user content (tool args, tool results, response bodies,
// prompts, memory contents) cannot reach the log file in release, even if an
// operator raises the runtime log level to Debug.
public static class SafeLog
{
    [Conditional("DEBUG")]
    public static void SensitiveTrace(this ILogger logger, string message, params object?[] args)
        => logger.LogTrace(message, args);

    [Conditional("DEBUG")]
    public static void SensitiveDebug(this ILogger logger, string message, params object?[] args)
        => logger.LogDebug(message, args);

    [Conditional("DEBUG")]
    public static void SensitiveInformation(this ILogger logger, string message, params object?[] args)
        => logger.LogInformation(message, args);

    [Conditional("DEBUG")]
    public static void SensitiveWarning(this ILogger logger, string message, params object?[] args)
        => logger.LogWarning(message, args);
}
