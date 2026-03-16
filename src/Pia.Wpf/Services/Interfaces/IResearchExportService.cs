using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IResearchExportService
{
    string BuildMarkdown(ResearchSession session);
    string BuildHtml(ResearchSession session);
    Task ExportAsMarkdownAsync(ResearchSession session, string filePath);
    Task ExportAsHtmlAsync(ResearchSession session, string filePath);
    Task ExportAsPdfAsync(ResearchSession session, string filePath);
}
