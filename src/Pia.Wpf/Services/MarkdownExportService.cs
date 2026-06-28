using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Converts an assistant answer's Markdown to a standalone HTML file with a single built-in basic
/// theme. Static (Markdig) render — no LLM call, no token cost. Reuses the Markdig pipeline pattern
/// already proven in <c>ResearchExportService</c>; intentionally self-contained (that service is
/// shaped around a removed research view).
/// </summary>
public sealed class MarkdownExportService : IMarkdownExportService
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly ISettingsService _settingsService;
    private readonly ILogger<MarkdownExportService> _logger;

    public MarkdownExportService(ISettingsService settingsService, ILogger<MarkdownExportService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<string> ExportAsync(
        string markdown, string? title, string fallbackTitle, string? workingSubpath, CancellationToken ct = default)
    {
        var effectiveTitle = !string.IsNullOrWhiteSpace(title)
            ? title!
            : DeriveTitle(markdown) ?? fallbackTitle;

        var html = ToHtml(markdown, effectiveTitle);

        var folder = await ResolveOutputFolderAsync(workingSubpath);
        Directory.CreateDirectory(folder);

        var path = NextAvailablePath(folder);
        await File.WriteAllTextAsync(path, html, Encoding.UTF8, ct);

        _logger.LogInformation("Exported assistant answer to HTML ({Chars} chars)", markdown.Length);
        _logger.SensitiveDebug("HTML export path: {Path}", path);
        return path;
    }

    public string ToHtml(string markdown, string title)
    {
        var htmlBody = Markdig.Markdown.ToHtml(markdown, _pipeline);
        var safeTitle = WebUtility.HtmlEncode(title);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>{{safeTitle}}</title>
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

    /// <summary>
    /// First Markdown heading (via the AST, so a <c>#</c> inside a code fence is ignored), else the first
    /// non-empty line with leading <c>#</c>/whitespace trimmed, else null (caller supplies the fallback).
    /// </summary>
    private static string? DeriveTitle(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var doc = Markdig.Markdown.Parse(markdown, _pipeline);
        var heading = doc.Descendants<HeadingBlock>().FirstOrDefault();
        if (heading?.Inline is not null)
        {
            var sb = new StringBuilder();
            foreach (var literal in heading.Inline.Descendants<LiteralInline>())
                sb.Append(literal.Content.ToString());
            var headingText = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(headingText)) return headingText;
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#').Trim();
            if (!string.IsNullOrEmpty(line)) return line;
        }
        return null;
    }

    /// <summary>Subfolder under the resolved working dir that exported answers are written into.</summary>
    private const string ExportsSubfolder = "Exports";

    private async Task<string> ResolveOutputFolderAsync(string? workingSubpath)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var configured = settings.AssistantFilesFolder;

        // Export is a user action, not an LLM write, so it works even when the file tools are disabled:
        // fall back to the default assistant files folder when no sandbox folder is configured.
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(AssistantWorkspace.DefaultRoot, ExportsSubfolder);

        var baseRoot = Path.GetFullPath(configured);

        // Scope to the active chat's working subdir when it resolves inside the sandbox and exists;
        // otherwise drop to the sandbox root (never widen beyond it).
        if (!string.IsNullOrWhiteSpace(workingSubpath)
            && Directory.Exists(baseRoot)
            && SafeFolderPath.TryResolveInsideAllowingAbsolute(baseRoot, workingSubpath, out var scoped)
            && Directory.Exists(scoped))
        {
            return Path.Combine(scoped, ExportsSubfolder);
        }

        return Path.Combine(baseRoot, ExportsSubfolder);
    }

    private static string NextAvailablePath(string folder)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(folder, $"pia-answer-{stamp}.html");
        if (!File.Exists(candidate)) return candidate;

        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"pia-answer-{stamp}-{i}.html");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
