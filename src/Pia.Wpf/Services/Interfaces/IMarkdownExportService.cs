namespace Pia.Services.Interfaces;

/// <summary>
/// Statically converts an assistant answer's Markdown to a standalone HTML file in the assistant
/// working directory. "Static" = local Markdig render, no LLM round-trip (no token cost). The
/// template is a single built-in basic theme for now.
/// </summary>
public interface IMarkdownExportService
{
    /// <summary>
    /// Renders <paramref name="markdown"/> to a full HTML document using the built-in template. The document is
    /// marked as AI-generated (generator / ai-generated meta, visible footer); <paramref name="aiModelLabel"/>
    /// names the producing provider and model when known.
    /// </summary>
    string ToHtml(string markdown, string title, string? aiModelLabel = null);

    /// <summary>
    /// Writes <paramref name="markdown"/> as an HTML file under the assistant working directory
    /// (scoped to <paramref name="workingSubpath"/> when set, else the sandbox root, else the default
    /// files folder — export works even when the file tools are disabled). Returns the absolute path.
    /// </summary>
    /// <param name="markdown">The answer Markdown to convert.</param>
    /// <param name="title">Optional title; when null/blank a title is derived from the Markdown, falling
    /// back to <paramref name="fallbackTitle"/>.</param>
    /// <param name="fallbackTitle">Localized default title (e.g. "Pia answer") used when none can be derived.</param>
    /// <param name="workingSubpath">Active chat's working dir relative to the sandbox root (forward slashes); null = root.</param>
    /// <param name="aiModelLabel">Provider · model that produced the answer, for the AI marking; null when unknown.</param>
    Task<string> ExportAsync(
        string markdown, string? title, string fallbackTitle, string? workingSubpath, string? aiModelLabel = null,
        CancellationToken ct = default);
}
