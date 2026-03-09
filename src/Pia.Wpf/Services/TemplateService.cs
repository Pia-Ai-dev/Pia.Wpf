using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared;

namespace Pia.Services;

public class TemplateService : JsonPersistenceService<List<OptimizationTemplate>>, ITemplateService
{
    private readonly ILogger<TemplateService> _logger;

    public event EventHandler? TemplatesChanged;

    private List<OptimizationTemplate>? _mergedTemplates;

    protected override string FileName => "templates.json";

    protected override List<OptimizationTemplate> CreateDefault() => [];

    public TemplateService(ILogger<TemplateService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<OptimizationTemplate>> GetTemplatesAsync()
    {
        if (_mergedTemplates is not null)
            return _mergedTemplates.AsReadOnly();

        var customTemplates = await LoadAsync();
        _mergedTemplates = CreateBuiltInTemplates();
        var builtInIds = _mergedTemplates.Select(t => t.Id).ToHashSet();
        _mergedTemplates.AddRange(customTemplates.Where(t => !builtInIds.Contains(t.Id)));

        return _mergedTemplates.AsReadOnly();
    }

    public async Task<OptimizationTemplate?> GetTemplateAsync(Guid id)
    {
        var templates = await GetTemplatesAsync();
        return templates.FirstOrDefault(t => t.Id == id);
    }

    public async Task<OptimizationTemplate> AddTemplateAsync(OptimizationTemplate template)
    {
        await GetTemplatesAsync();
        template.IsBuiltIn = false;
        _mergedTemplates!.Add(template);
        await SaveCustomTemplatesAsync();
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
        return template;
    }

    public async Task UpdateTemplateAsync(OptimizationTemplate template)
    {
        await GetTemplatesAsync();
        var existing = _mergedTemplates!.FirstOrDefault(t => t.Id == template.Id);
        if (existing is null)
            throw new InvalidOperationException($"Template with id {template.Id} not found");

        if (existing.IsBuiltIn)
            throw new InvalidOperationException("Cannot modify built-in templates");

        var index = _mergedTemplates!.IndexOf(existing);
        template.ModifiedAt = DateTime.UtcNow;
        _mergedTemplates![index] = template;
        await SaveCustomTemplatesAsync();
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteTemplateAsync(Guid id)
    {
        await GetTemplatesAsync();
        var template = _mergedTemplates!.FirstOrDefault(t => t.Id == id);
        if (template is null)
            return;

        if (template.IsBuiltIn)
            throw new InvalidOperationException("Cannot delete built-in templates");

        _mergedTemplates!.Remove(template);
        await SaveCustomTemplatesAsync();
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<string> GeneratePromptAsync(string styleDescription)
    {
        // Placeholder - will be implemented when AiClientService exists
        return Task.FromResult($"Optimize the following text to match this style:\n\n{styleDescription}");
    }

    private async Task SaveCustomTemplatesAsync()
    {
        var customTemplates = _mergedTemplates!.Where(t => !t.IsBuiltIn).ToList();
        await SaveAsync(customTemplates);
    }

    private static List<OptimizationTemplate> CreateBuiltInTemplates()
    {
        return BuiltInTemplates.All.Select(t => new OptimizationTemplate
        {
            Id = new Guid(t.Id),
            Name = t.Name,
            Prompt = t.Prompt,
            Description = t.Description,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        }).ToList();
    }
}
