using System.ClientModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Anthropic.Exceptions;
using Pia.Helpers;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;

namespace Pia.Services;

internal static class StepInterruptionClassifier
{
    private const string ToolFaultKey = "Pia.StepInterruption.ToolFault";

    internal static StepInterruption? Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var chain = FailureMapper.Unwrap(ex, depth: 0).ToList();

        if (chain.Any(e => e.Data.Contains(ToolFaultKey)))
            return null;
        if (chain.Any(e => e is CreditLimitException))
            return StepInterruption.Credit;
        if (chain.Any(IsTimeout))
            return StepInterruption.Timeout;
        if (NetworkFailure.IsOffline(ex) || chain.Any(e => e is HttpIOException or SocketException or AnthropicIOException))
            return StepInterruption.Offline;
        if (chain.Any(e => e is PiaCloudStreamException or AnthropicSseException))
            return StepInterruption.Provider;
        if (OutermostStatus(chain) is { } transient && transient)
            return StepInterruption.Provider;
        return null;
    }

    /// <summary>Marks an exception that left a tool handler; always false, for use as an exception filter.</summary>
    internal static bool MarkToolFault(Exception ex)
    {
        ex.Data[ToolFaultKey] = true;
        return false;
    }

    private static bool IsTimeout(Exception ex) =>
        ex is LlmTimeoutException
        || ex is OperationCanceledException { InnerException: TimeoutException };

    // The outermost status-bearing exception decides: an SDK exception carries its own status and wraps a
    // status-less HttpRequestException that must not override it. A status-0 SDK exception got no response, so it defers.
    private static bool? OutermostStatus(List<Exception> chain)
    {
        foreach (var e in chain)
        {
            switch (e)
            {
                case AnthropicRateLimitException or Anthropic5xxException:
                    return true;
                case AnthropicApiException api:
                    return api.StatusCode == HttpStatusCode.RequestTimeout;
                case ClientResultException result when result.Status != 0:
                    return IsTransient(result.Status);
                case HttpRequestException http:
                    return http.StatusCode is not { } status || IsTransient((int)status);
            }
        }
        return null;
    }

    private static bool IsTransient(int status) => status is 408 or 429 or >= 500;
}
