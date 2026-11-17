using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>Answers the tool call a parked chat job is waiting on, with a chat card's four choices.</summary>
public interface IRunToolAnswerService
{
    Task<bool> AnswerParkedToolAsync(Guid runId, ToolDecision decision, CancellationToken ct = default);
}
