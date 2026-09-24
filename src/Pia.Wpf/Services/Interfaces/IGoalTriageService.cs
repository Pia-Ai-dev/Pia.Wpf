using Pia.Models;

namespace Pia.Services.Interfaces;

public enum GoalTriageVerdict
{
    NeedsPlan,
    AnswerDirectly,
}

public interface IGoalTriageService
{
    /// <summary>Never throws except on <paramref name="ct"/>; every other failure is
    /// <see cref="GoalTriageVerdict.NeedsPlan"/>.</summary>
    Task<GoalTriageVerdict> ClassifyAsync(string goal, AiProvider provider, CancellationToken ct);
}
