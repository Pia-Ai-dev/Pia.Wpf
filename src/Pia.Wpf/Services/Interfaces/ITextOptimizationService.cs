using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITextOptimizationService
{
    Task<OptimizationSession> OptimizeTextAsync(
        string inputText,
        Guid templateId,
        Guid? providerId = null,
        string targetLanguage = "EN",
        CancellationToken cancellationToken = default);

    Task<bool> ValidateInputAsync(string inputText, Guid templateId);

    Task<string> GeneratePromptAsync(string styleDescription, Guid? providerId = null);
}
