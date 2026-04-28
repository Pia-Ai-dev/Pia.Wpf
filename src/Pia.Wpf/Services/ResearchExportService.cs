using System.IO;
using System.Text;
using Markdig;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchExportService : IResearchExportService
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string BuildMarkdown(ResearchSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Research: {session.Query}");
        sb.AppendLine();
        sb.AppendLine($"*{session.CreatedAt:f}*");
        sb.AppendLine();

        foreach (var step in session.Steps)
        {
            sb.AppendLine($"## Step {step.StepNumber}: {step.Title}");
            sb.AppendLine();
            sb.AppendLine(step.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string BuildHtml(ResearchSession session)
    {
        var markdown = BuildMarkdown(session);
        var htmlBody = Markdig.Markdown.ToHtml(markdown, _pipeline);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Research: {{System.Net.WebUtility.HtmlEncode(session.Query)}}</title>
                <style>
                    body {
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                        line-height: 1.6;
                        max-width: 900px;
                        margin: 0 auto;
                        padding: 2rem;
                        color: #333;
                    }
                    h1 { color: #1a1a2e; border-bottom: 2px solid #e0e0e0; padding-bottom: 0.3em; }
                    h2 { color: #16213e; margin-top: 1.5em; }
                    h3 { color: #0f3460; }
                    code {
                        background: #f4f4f4;
                        padding: 0.2em 0.4em;
                        border-radius: 3px;
                        font-size: 0.9em;
                    }
                    pre {
                        background: #f4f4f4;
                        padding: 1em;
                        border-radius: 6px;
                        overflow-x: auto;
                    }
                    pre code { background: transparent; padding: 0; }
                    blockquote {
                        border-left: 4px solid #4a90d9;
                        margin: 1em 0;
                        padding: 0.5em 1em;
                        color: #555;
                        background: #f8f9fa;
                    }
                    table {
                        border-collapse: collapse;
                        width: 100%;
                        margin: 1em 0;
                    }
                    th, td {
                        border: 1px solid #ddd;
                        padding: 8px 12px;
                        text-align: left;
                    }
                    th { background: #f4f4f4; font-weight: 600; }
                </style>
            </head>
            <body>
            {{htmlBody}}
            </body>
            </html>
            """;
    }

    public async Task ExportAsMarkdownAsync(ResearchSession session, string filePath)
    {
        var markdown = BuildMarkdown(session);
        await File.WriteAllTextAsync(filePath, markdown, Encoding.UTF8);
    }

    public async Task ExportAsHtmlAsync(ResearchSession session, string filePath)
    {
        var html = BuildHtml(session);
        await File.WriteAllTextAsync(filePath, html, Encoding.UTF8);
    }
}
