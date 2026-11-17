using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>A tool call a running chat job waits on. <c>DisplayArgs</c> is user content: render, never log.</summary>
public sealed record RunToolQuestion(
    Guid RequestId, Guid RunId, Guid ChatId, Guid? StepId, string ToolName, Guid? PluginId, string? DisplayArgs);

/// <summary>Is the chat displayed in a window right now?</summary>
public interface IChatAttendance
{
    bool IsAttended(Guid chatId);
}

/// <summary>Carries a running chat job's questions to its block and the answers back.</summary>
public interface IRunAskBroker
{
    /// <summary>Raised with the run id when a run's pending question or recorded user-input question changes.</summary>
    event EventHandler<Guid>? Changed;

    RunToolQuestion? GetPending(Guid runId);

    /// <summary>The answer, or null when the job must park: nobody is looking, or nobody answered in time.</summary>
    /// <param name="setClockRunning">Told false while the step actually waits, and true when the wait ends.</param>
    Task<ToolDecision?> AskAsync(RunToolQuestion question, Action<bool>? setClockRunning, CancellationToken ct);

    /// <summary>False when the question is no longer open (answered, timed out or stopped).</summary>
    bool Answer(Guid runId, Guid requestId, ToolDecision decision);

    /// <summary>The text of a request_user_input question the job parked on, kept for the block.</summary>
    void SetUserInputQuestion(Guid runId, string? question);

    string? GetUserInputQuestion(Guid runId);
}

/// <summary>What one chat-job step needs to ask on its block and stop its budget clock while it waits.</summary>
public sealed record RunAskContext(
    IRunAskBroker Broker, Guid RunId, Guid ChatId, Guid? StepId, Action<bool> SetClockRunning, CancellationToken Ct);
