using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
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
        string markdown, string? title, string fallbackTitle, string? workingSubpath, string? aiModelLabel = null,
        CancellationToken ct = default)
    {
        var effectiveTitle = !string.IsNullOrWhiteSpace(title)
            ? title!
            : DeriveTitle(markdown) ?? fallbackTitle;

        var html = ToHtml(markdown, effectiveTitle, aiModelLabel);

        var folder = await ResolveOutputFolderAsync(workingSubpath);
        Directory.CreateDirectory(folder);

        var path = NextAvailablePath(folder);
        await File.WriteAllTextAsync(path, html, Encoding.UTF8, ct);

        _logger.LogInformation("Exported assistant answer to HTML ({Chars} chars)", markdown.Length);
        _logger.SensitiveDebug("HTML export path: {Path}", path);
        return path;
    }

    /// <summary>
    /// Markdown, not HTML: the file lands in the vault's <c>sources/</c> RAW layer, where auto-ingest picks
    /// it up and compiles it into the topic pages. The answer is written verbatim.
    /// </summary>
    public async Task<string> ExportToVaultAsync(
        string markdown, string fileName, string fallbackTitle, CancellationToken ct = default)
    {
        var folder = await ResolveVaultExportsFolderAsync();
        Directory.CreateDirectory(folder);

        var stem = SanitizeStem(StripExtension(fileName, MarkdownExtension), fallbackTitle);
        var path = NextAvailableNamedPath(folder, stem, MarkdownExtension);
        await File.WriteAllTextAsync(path, markdown, Encoding.UTF8, ct);

        _logger.LogInformation("Exported assistant answer to the vault ({Chars} chars)", markdown.Length);
        _logger.SensitiveDebug("Vault export path: {Path}", path);
        return path;
    }

    /// <summary>Writes the HTML render to the exact path the user picked — no renaming, no collision suffix.</summary>
    public async Task ExportToPathAsync(
        string markdown, string absolutePath, string fallbackTitle, string? aiModelLabel = null,
        CancellationToken ct = default)
    {
        var html = ToHtml(markdown, DeriveTitle(markdown) ?? fallbackTitle, aiModelLabel);

        var folder = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(absolutePath, html, Encoding.UTF8, ct);

        _logger.LogInformation("Exported assistant answer to a chosen path ({Chars} chars)", markdown.Length);
        _logger.SensitiveDebug("External export path: {Path}", absolutePath);
    }

    public string SuggestFileName(string markdown, string fallbackTitle) =>
        SanitizeStem(DeriveTitle(markdown), fallbackTitle);

    public string ToHtml(string markdown, string title, string? aiModelLabel = null)
    {
        var htmlBody = Markdig.Markdown.ToHtml(markdown, _pipeline);
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeGenerator = WebUtility.HtmlEncode(AppVersionInfo.Generator);
        var safeModel = string.IsNullOrWhiteSpace(aiModelLabel) ? null : WebUtility.HtmlEncode(aiModelLabel.Trim());
        var modelMeta = safeModel is null ? string.Empty : $"\n    <meta name=\"ai-model\" content=\"{safeModel}\">";
        var modelFooter = safeModel is null ? string.Empty : $" · {safeModel}";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <meta name="generator" content="{{safeGenerator}}">
                <meta name="ai-generated" content="true">{{modelMeta}}
                <title>{{safeTitle}}</title>
                {{ThemeInitScript}}
                {{Styles}}
            </head>
            <body>
                {{ThemeToggle}}
                <main class="pia-doc">
            {{htmlBody}}
                    <footer class="pia-doc-footer"><span class="pia-ai-mark">AI-generated content · {{safeGenerator}}{{modelFooter}}</span><br><a class="pia-footer-link" href="https://pia-ai.de"><span class="pia-wordmark">Pia</span> — Personal Intelligent Assistant</a></footer>
                </main>
                {{ThemeScript}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Brand styling for exported answers — mirrors Pia.Web's "Digital Editorial" look (warm
    /// paper / ink palette, blue accent, serif-display headings, dot-grid + grain texture) via
    /// CSS-variable theming. Self-contained: no external stylesheet and no web fonts — Technor
    /// and Outfit degrade to a serif / system-sans stack so the file renders identically offline.
    /// </summary>
    private const string Styles = """
        <style>
        :root {
            --bg: #FAFAF7;
            --surface: #FFFFFF;
            --ink: #1C1917;
            --ink-soft: #44403C;
            --ink-muted: #78716C;
            --accent: #0078D4;
            --rule: #E7E5E4;
            --code-bg: #F5F5F0;
            --quote-bg: rgba(0, 120, 212, 0.05);
            --dot: rgba(28, 25, 23, 0.07);
            --selection: rgba(0, 120, 212, 0.20);
            --shadow: rgba(0, 120, 212, 0.08);
            --grain: 0.022;
            --font-display: Georgia, "Times New Roman", serif;
            --font-body: system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            --font-mono: ui-monospace, "Cascadia Code", "Segoe UI Mono", Consolas, monospace;
        }
        html.dark {
            --bg: #0C0C0C;
            --surface: #161614;
            --ink: #E7E5E4;
            --ink-soft: #D6D3D1;
            --ink-muted: #A8A29E;
            --accent: #60CDFF;
            --rule: #292524;
            --code-bg: #1F1E1C;
            --quote-bg: rgba(96, 205, 255, 0.06);
            --dot: rgba(231, 229, 228, 0.04);
            --selection: rgba(96, 205, 255, 0.20);
            --shadow: rgba(96, 205, 255, 0.06);
            --grain: 0.04;
        }
        * { box-sizing: border-box; }
        html { scroll-behavior: smooth; }
        body {
            margin: 0;
            min-height: 100vh;
            background-color: var(--bg);
            background-image: radial-gradient(circle, var(--dot) 1px, transparent 1px);
            background-size: 24px 24px;
            color: var(--ink-soft);
            font-family: var(--font-body);
            font-size: 17px;
            line-height: 1.7;
            -webkit-font-smoothing: antialiased;
            text-rendering: optimizeLegibility;
            transition: background-color 0.3s ease, color 0.3s ease;
        }
        body::after {
            content: "";
            position: fixed;
            inset: 0;
            z-index: 9999;
            pointer-events: none;
            opacity: var(--grain);
            background-image: url("data:image/svg+xml,%3Csvg viewBox='0 0 256 256' xmlns='http://www.w3.org/2000/svg'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='4' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E");
            background-size: 256px 256px;
        }
        .pia-doc {
            position: relative;
            z-index: 1;
            max-width: 820px;
            margin: 3rem auto;
            padding: 3rem 3.5rem;
            background: var(--surface);
            border: 1px solid var(--rule);
            border-radius: 16px;
            box-shadow: 0 8px 30px -12px var(--shadow);
        }
        h1, h2, h3, h4 {
            font-family: var(--font-display);
            color: var(--accent);
            font-weight: 600;
            line-height: 1.2;
            letter-spacing: -0.01em;
        }
        h1 {
            font-size: 2.2rem;
            margin: 0 0 0.8em;
            padding-bottom: 0.3em;
            border-bottom: 1px solid var(--rule);
        }
        h2 { font-size: 1.55rem; margin: 1.8em 0 0.6em; }
        h3 { font-size: 1.25rem; margin: 1.6em 0 0.5em; }
        h4 { font-size: 1.05rem; margin: 1.4em 0 0.5em; }
        p { margin: 0 0 1.1em; }
        a {
            color: var(--accent);
            text-decoration: none;
            border-bottom: 1px solid transparent;
            transition: border-color 0.2s ease;
        }
        a:hover { border-bottom-color: var(--accent); }
        strong { color: var(--ink); font-weight: 600; }
        ul, ol { padding-left: 1.4em; margin: 0 0 1.1em; }
        li { margin: 0.3em 0; }
        code {
            font-family: var(--font-mono);
            background: var(--code-bg);
            color: var(--ink);
            padding: 0.15em 0.4em;
            border-radius: 4px;
            font-size: 0.88em;
        }
        pre {
            background: var(--code-bg);
            border: 1px solid var(--rule);
            padding: 1.1em 1.25em;
            border-radius: 10px;
            overflow-x: auto;
            font-size: 0.875em;
            line-height: 1.55;
        }
        pre code { background: transparent; padding: 0; font-size: inherit; }
        blockquote {
            margin: 1.5em 0;
            padding: 0.75em 1.25em;
            border-left: 3px solid var(--accent);
            border-radius: 0 8px 8px 0;
            background: var(--quote-bg);
            color: var(--ink-soft);
        }
        blockquote p:last-child { margin-bottom: 0; }
        table {
            border-collapse: collapse;
            width: 100%;
            margin: 1.5em 0;
            font-size: 0.95em;
        }
        th, td { border: 1px solid var(--rule); padding: 10px 14px; text-align: left; }
        th { background: var(--code-bg); color: var(--ink); font-weight: 600; }
        hr { border: none; border-top: 1px solid var(--rule); margin: 2em 0; }
        img { max-width: 100%; height: auto; border-radius: 8px; }
        ::selection { background: var(--selection); }
        .pia-doc-footer {
            margin-top: 3rem;
            padding-top: 1.5rem;
            border-top: 1px solid var(--rule);
            text-align: right;
            font-size: 0.9rem;
        }
        .pia-footer-link {
            color: var(--ink-muted);
            text-decoration: none;
            border-bottom: none;
            transition: color 0.2s ease;
        }
        .pia-footer-link:hover { color: var(--ink); border-bottom: none; }
        .pia-ai-mark { color: var(--ink-muted); font-size: 0.8rem; }
        .pia-wordmark { font-family: var(--font-display); font-weight: 600; color: var(--accent); font-size: 1.1rem; }
        .theme-toggle {
            position: fixed;
            top: 1rem;
            right: 1rem;
            z-index: 50;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            width: 40px;
            height: 40px;
            padding: 0;
            border: 1px solid var(--rule);
            border-radius: 10px;
            background: var(--surface);
            color: var(--ink-muted);
            cursor: pointer;
            transition: color 0.2s ease, border-color 0.2s ease, background 0.2s ease;
        }
        .theme-toggle:hover { color: var(--ink); border-color: var(--accent); }
        .theme-toggle svg { width: 20px; height: 20px; }
        .theme-toggle .icon-sun { display: none; }
        html.dark .theme-toggle .icon-sun { display: block; }
        html.dark .theme-toggle .icon-moon { display: none; }
        @media (max-width: 640px) {
            body { font-size: 16px; }
            .pia-doc { margin: 1rem; padding: 1.75rem 1.4rem; border-radius: 12px; }
            h1 { font-size: 1.8rem; }
        }
        @media print {
            .theme-toggle { display: none; }
            body::after { display: none; }
            body { background: #fff; }
            .pia-doc { border: none; box-shadow: none; margin: 0; max-width: none; }
        }
        </style>
        """;

    /// <summary>Pre-paint theme bootstrap (in &lt;head&gt;): apply the saved or OS-preferred theme before first render to avoid a flash.</summary>
    private const string ThemeInitScript = """
        <script>
            (function () {
                try {
                    var t = localStorage.getItem("pia-export-theme");
                    if (t === "dark" || (!t && window.matchMedia("(prefers-color-scheme: dark)").matches)) {
                        document.documentElement.classList.add("dark");
                    }
                } catch (e) { /* localStorage unavailable (file:// in some browsers) */ }
            })();
        </script>
        """;

    /// <summary>Sun/moon theme toggle button — mirrors the switcher on the Pia.Web site.</summary>
    private const string ThemeToggle = """
        <button id="theme-toggle" class="theme-toggle" type="button" aria-label="Toggle color theme">
                <svg class="icon-sun" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                    <path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z"/>
                </svg>
                <svg class="icon-moon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                    <path stroke-linecap="round" stroke-linejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z"/>
                </svg>
            </button>
        """;

    /// <summary>Toggle behaviour (end of &lt;body&gt;): flip the theme class and remember the choice.</summary>
    private const string ThemeScript = """
        <script>
            (function () {
                var btn = document.getElementById("theme-toggle");
                if (!btn) return;
                btn.addEventListener("click", function () {
                    var isDark = document.documentElement.classList.toggle("dark");
                    try { localStorage.setItem("pia-export-theme", isDark ? "dark" : "light"); } catch (e) { /* ignore */ }
                });
            })();
        </script>
        """;

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

    private async Task<string> ResolveVaultExportsFolderAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return VaultExportsFolderFor(settings.AssistantFilesFolder);
    }

    /// <summary>
    /// Vault export folder for a configured files folder: under the <c>sources/</c> RAW layer, which is what
    /// auto-ingest watches — outside it the file is only chunk-indexed, never compiled into a topic page. A
    /// blank setting is the default install, and it still has to land inside the vault, not beside it.
    /// </summary>
    internal static string VaultExportsFolderFor(string? configuredFilesFolder)
    {
        var root = string.IsNullOrWhiteSpace(configuredFilesFolder)
            ? AssistantWorkspace.DefaultRoot
            : Path.GetFullPath(configuredFilesFolder);

        return Path.Combine(AssistantWorkspace.VaultRootFor(root), SourcesSubfolder, ExportsSubfolder);
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

    private static string NextAvailableNamedPath(string folder, string stem, string extension)
    {
        var candidate = Path.Combine(folder, $"{stem}{extension}");
        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{stem}-{i}{extension}");
        return candidate;
    }

    /// <summary>Drops the extension the writer is about to add, so a typed "notes.md" cannot become "notes.md.md".</summary>
    private static string StripExtension(string fileName, string extension)
    {
        var typed = fileName.Trim();
        return typed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? typed[..^extension.Length]
            : typed;
    }

    /// <summary>
    /// Turns user-typed text into a bare filename. Every invalid char — including the separators and
    /// the drive colon — becomes '_', so the result cannot walk out of the Exports folder.
    /// </summary>
    private static string SanitizeStem(string? name, string fallback)
    {
        var cleaned = Clean(name);
        if (cleaned.Length > 0) return cleaned;

        cleaned = Clean(fallback);
        return cleaned.Length > 0 ? cleaned : "pia-answer";

        static string Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

            // Windows silently drops a trailing dot or space, which would turn "x." into a second "x".
            var trimmed = sb.ToString().Trim().TrimEnd('.', ' ');
            return trimmed.Length > MaxStemLength ? trimmed[..MaxStemLength].TrimEnd('.', ' ') : trimmed;
        }
    }

    private const int MaxStemLength = 100;
    private const string MarkdownExtension = ".md";

    /// <summary>The vault's RAW ingest layer.</summary>
    private const string SourcesSubfolder = "sources";
}
