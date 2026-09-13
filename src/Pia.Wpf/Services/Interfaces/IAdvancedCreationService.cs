using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Runs the Advanced Creation interview: the model asks the few things it cannot infer, then produces
/// the same draft the one-shot generator would have. Kept off <see cref="ITextOptimizationService"/>,
/// which already carries four unrelated generators.
/// </summary>
public interface IAdvancedCreationService
{
    /// <summary>How many times the model may ask before it is told to produce the draft regardless.</summary>
    int MaxAskTurns { get; }

    Task<AdvancedCreationTurn> StartAsync(
        AdvancedCreationSession session,
        string opening,
        CancellationToken cancellationToken = default);

    Task<AdvancedCreationTurn> AnswerAsync(
        AdvancedCreationSession session,
        IReadOnlyDictionary<string, string> answers,
        IReadOnlyList<string> skipped,
        CancellationToken cancellationToken = default);

    /// <summary>Re-runs the last turn after a failure, without answering anything again.</summary>
    Task<AdvancedCreationTurn> RetryTurnAsync(
        AdvancedCreationSession session,
        CancellationToken cancellationToken = default);
}
