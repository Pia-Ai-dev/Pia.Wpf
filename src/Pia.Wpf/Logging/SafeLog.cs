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
//
// Every method here emits at Debug or Trace, whatever its own name suggests about severity. That is what
// lets the diagnostics export drop this whole family with one level test instead of a deny-list of call
// sites: in the log file, DBUG and TRCE mean "written by a debug build". SafeLogLevelTests holds the line.
public static class SafeLog
{
    /// <summary>True when this build still compiles the calls below in, so its log can hold user content.</summary>
    public static bool SensitiveLoggingCompiledIn { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    [Conditional("DEBUG")]
    public static void SensitiveTrace(this ILogger logger, string message, params object?[] args)
        => logger.LogTrace(message, args);

    [Conditional("DEBUG")]
    public static void SensitiveDebug(this ILogger logger, string message, params object?[] args)
        => logger.LogDebug(message, args);

    [Conditional("DEBUG")]
    public static void SensitiveInformation(this ILogger logger, string message, params object?[] args)
        => logger.LogDebug(message, args);

    [Conditional("DEBUG")]
    public static void SensitiveWarning(this ILogger logger, string message, params object?[] args)
        => logger.LogDebug(message, args);
}
