using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITemplateService
{
    event EventHandler? TemplatesChanged;

    Task<IReadOnlyList<OptimizationTemplate>> GetTemplatesAsync();
    Task<OptimizationTemplate?> GetTemplateAsync(Guid id);
    Task<OptimizationTemplate> AddTemplateAsync(OptimizationTemplate template);
    Task UpdateTemplateAsync(OptimizationTemplate template);
    Task DeleteTemplateAsync(Guid id);
    Task<string> GeneratePromptAsync(string styleDescription);
}
