using System.IO;
using System.Net.Http;
using Pia.Models;
using Pia.Services.Exceptions;

namespace Pia.Services;

/// <summary>
/// Turns a failure into a <see cref="PiaFailure"/>. Two entry points because there are two kinds of caller:
/// one that already knows which failure this is, and one holding an exception.
/// </summary>
public static class FailureMapper
{
    /// <summary>A reason no arm recognises. The panel still shows the reason text itself.</summary>
    public const string UnclassifiedCode = "Unclassified";

    /// <summary>
    /// The app-owned failure constants, each vouched for by the code that raises it. Matched on the constant
    /// by reference to its declaration, never on a substring — a caller's arbitrary message must fall through
    /// to null rather than be guessed at.
    /// </summary>
    public static PiaFailure? ForReason(string? reason) => reason switch
    {
        null or "" => null,
        AgentStepTools.UndetailedFailure => new(FailureLayer.Tool, "Undetailed", false),
        AgentStepTools.EmptyResponseFailure => new(FailureLayer.Provider, "EmptyResponse", false),
        HeadlessRunLauncher.WorkspaceSetupFailure => new(FailureLayer.Workspace, "WorkspaceSetup", false),
        HeadlessRunLauncher.ShutdownInterruptedFailure => new(FailureLayer.Cancelled, "Interrupted", false),
        AgentRunOrchestrator.SupersededFailureReason => new(FailureLayer.Cancelled, "Superseded", false),
        ScheduledJobService.NoProviderFailureReason => new(FailureLayer.Provider, "NoProvider", true),
        _ => null,
    };

    /// <summary>
    /// Keyed on exception TYPE, never on message text — the rule
    /// <c>ScheduledJobService.IsPreModelFailure</c> states for widening itself. Anything unrecognised is
    /// <see cref="FailureLayer.Unclassified"/> and never safe to re-run.
    /// <para>
    /// Walks the inner chain, because nothing arrives bare: a refused connection reaches the orchestrator as
    /// AggregateException → ClientResultException → HttpRequestException → SocketException, and matching only
    /// the outermost type classified every real transport failure as Unclassified.
    /// </para>
    /// </summary>
    public static PiaFailure ForException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        foreach (var candidate in Unwrap(ex, depth: 0))
        {
            var failure = Classify(candidate);
            if (failure is not null) return failure;
        }

        return new(FailureLayer.Unclassified, UnclassifiedCode, false);
    }

    // Depth-first, outermost first, so the most specific wrapper still wins over the socket error at the
    // bottom. Bounded because an exception graph is caller-supplied.
    private static IEnumerable<Exception> Unwrap(Exception ex, int depth)
    {
        if (depth > 8) yield break;
        yield return ex;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var nested in Unwrap(inner, depth + 1))
                    yield return nested;
            yield break;
        }

        if (ex.InnerException is { } single)
            foreach (var nested in Unwrap(single, depth + 1))
                yield return nested;
    }

    private static PiaFailure? Classify(Exception ex) => ex switch
    {
        // The only arm that can vouch for SafeToReRun: it is thrown before the stub chat is written.
        PreModelLaunchException => new(FailureLayer.Provider, "NoProvider", true),
        LlmTimeoutException => new(FailureLayer.Provider, "Timeout", false),
        LlmTruncatedException => new(FailureLayer.Provider, "Truncated", false),
        BrowserLaunchException => new(FailureLayer.Tool, "BrowserLaunch", false),
        HttpRequestException => new(FailureLayer.Endpoint, "Transport", false),
        TaskCanceledException or OperationCanceledException => new(FailureLayer.Cancelled, "Cancelled", false),
        UnauthorizedAccessException => new(FailureLayer.Workspace, "AccessDenied", false),
        IOException => new(FailureLayer.Workspace, "Io", false),
        _ => null,
    };
}
