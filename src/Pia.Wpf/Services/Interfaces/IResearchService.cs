using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IResearchService
{
    Task ExecuteResearchAsync(ResearchSession session, AiProvider provider, CancellationToken ct);
}
