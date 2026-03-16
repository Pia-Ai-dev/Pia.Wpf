using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
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

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Research: {System.Net.WebUtility.HtmlEncode(session.Query)}</title>
                <style>
                    body {{
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                        line-height: 1.6;
                        max-width: 900px;
                        margin: 0 auto;
                        padding: 2rem;
                        color: #333;
                    }}
                    h1 {{ color: #1a1a2e; border-bottom: 2px solid #e0e0e0; padding-bottom: 0.3em; }}
                    h2 {{ color: #16213e; margin-top: 1.5em; }}
                    h3 {{ color: #0f3460; }}
                    code {{
                        background: #f4f4f4;
                        padding: 0.2em 0.4em;
                        border-radius: 3px;
                        font-size: 0.9em;
                    }}
                    pre {{
                        background: #f4f4f4;
                        padding: 1em;
                        border-radius: 6px;
                        overflow-x: auto;
                    }}
                    pre code {{ background: transparent; padding: 0; }}
                    blockquote {{
                        border-left: 4px solid #4a90d9;
                        margin: 1em 0;
                        padding: 0.5em 1em;
                        color: #555;
                        background: #f8f9fa;
                    }}
                    table {{
                        border-collapse: collapse;
                        width: 100%;
                        margin: 1em 0;
                    }}
                    th, td {{
                        border: 1px solid #ddd;
                        padding: 8px 12px;
                        text-align: left;
                    }}
                    th {{ background: #f4f4f4; font-weight: 600; }}
                </style>
            </head>
            <body>
            {htmlBody}
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

    public async Task ExportAsPdfAsync(ResearchSession session, string filePath)
    {
        var html = BuildHtml(session);

        // Use WPF FlowDocument for PDF-like output via XPS
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var flowDocument = new FlowDocument
            {
                PageWidth = 816, // 8.5" at 96 DPI
                PageHeight = 1056, // 11" at 96 DPI
                PagePadding = new Thickness(72), // 0.75" margins
                ColumnWidth = double.MaxValue,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13
            };

            BuildFlowDocument(flowDocument, session);

            // Write to XPS first, then rename (XPS is close to PDF for local use)
            var xpsPath = filePath;
            using var xpsDocument = new XpsDocument(xpsPath, FileAccess.Write);
            var writer = XpsDocument.CreateXpsDocumentWriter(xpsDocument);
            writer.Write(((IDocumentPaginatorSource)flowDocument).DocumentPaginator);
        });
    }

    private static void BuildFlowDocument(FlowDocument doc, ResearchSession session)
    {
        // Title
        var title = new Paragraph(new Run($"Research: {session.Query}"))
        {
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        doc.Blocks.Add(title);

        // Date
        var date = new Paragraph(new Run(session.CreatedAt.ToString("f")))
        {
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 16)
        };
        doc.Blocks.Add(date);

        // Steps
        foreach (var step in session.Steps)
        {
            var heading = new Paragraph(new Run($"Step {step.StepNumber}: {step.Title}"))
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 4)
            };
            doc.Blocks.Add(heading);

            if (!string.IsNullOrWhiteSpace(step.Content))
            {
                foreach (var line in step.Content.Split('\n'))
                {
                    var para = new Paragraph(new Run(line))
                    {
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    doc.Blocks.Add(para);
                }
            }
        }
    }
}
