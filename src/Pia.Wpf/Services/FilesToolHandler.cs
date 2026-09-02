using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Exposes read/write/delete tools over a user-configured sandbox folder.
/// Every path supplied by the model is validated against the sandbox root
/// via <see cref="SafeFolderPath.TryResolveInside"/> immediately before the
/// filesystem call, so traversal sequences like "..\..\demo" cannot escape.
/// </summary>
public class FilesToolHandler : IFilesToolHandler
{
    private const int MaxWriteChars = 512 * 1024;         // 512 K chars write cap
    private const int MaxListEntries = 500;

    // search_files caps (mirror the MaxListEntries convention + its truncation message).
    private const int MaxMatches = 500;          // hard stop on collected matches
    private const int MaxFilesScanned = 20_000;  // hard stop on files visited during the walk
    private const int MaxExtractions = 200;      // hard stop on .docx/.xlsx/.msg/.eml extractions per search
    private const int SkippedNamesShown = 5;     // unsearchable files named in the diagnostic before "…"

    // find_files result window.
    private const int DefaultFindLimit = 100;
    private const int MaxFindLimit = 500;

    // read_file windowing / cap regime.
    // Input ceiling: raw file bytes loaded into memory. Reconciled with DroppedFileReader
    // (1 MB extracted text for structured docs, 8 MB raw container) — we adopt the same
    // 1 MB ceiling for plain-text reads so the two read paths share one effective limit.
    private const long MaxReadFileBytes = DroppedFileReader.MaxTextBytes; // 1 MB raw text ceiling
    private const int DefaultReadLimit = 500;             // default window line count
    private const int MaxReadLimit = 2000;                // hard cap on window line count
    private const int MaxFormattedWindowChars = 100 * 1024; // ~100K-char cap on formatted output

    // docx/xlsx patch-in-place caps: bound how much of a single write_file call's OpenXml mutation
    // work can cost, and guard against a write built from a partial (windowed) read of the document.
    private static readonly DocxPatcher.PatchLimits DocxPatchLimits = new(MaxTouchedNodes: 2000, MaxRemovedAbsolute: 50, MaxRemovedFraction: 0.4);
    private static readonly XlsxPatcher.PatchLimits XlsxPatchLimits = new(MaxTouchedCells: 5000);

    // Macro-enabled/template OOXML variants are read-only: regenerating any part of a macro-bearing
    // package risks losing or mishandling the macro project, and a template's own semantics (.dotx
    // opens as a new untitled document) don't map onto "edit this exact file."
    private static readonly HashSet<string> MacroOrTemplateWriteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docm", ".dotm", ".dotx", ".xlsm", ".xltm", ".xltx",
    };

    // read_file renders these rather than returning the file's own bytes, so an edit round-trip would
    // write the rendering over the original. Unlike docx/xlsx there is no patch-in-place path to add.
    private static readonly HashSet<string> RenderedReadOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msg", ".eml",
    };

    private readonly ISettingsService _settingsService;
    private readonly IFileStalenessStore _stalenessStore;
    private readonly ILogger<FilesToolHandler> _logger;
    private volatile string? _currentFolder;
    private volatile bool _toolsEnabled = true;
    private volatile string? _activeUiWorkingSubpath;

    /// <summary>
    /// Working subpath of the chat currently shown in the UI, used to scope
    /// <see cref="ListRelativeFiles"/> (the <c>@Files</c> autocomplete) — which runs OUTSIDE any
    /// turn, so it cannot read the per-turn <see cref="TaskAmbient"/>. The view model pushes this
    /// on active-chat change / re-point. Null/empty = sandbox root.
    /// </summary>
    public string? ActiveUiWorkingSubpath
    {
        get => _activeUiWorkingSubpath;
        set => _activeUiWorkingSubpath = value;
    }

    public FilesToolHandler(ISettingsService settingsService, IFileStalenessStore stalenessStore, ILogger<FilesToolHandler> logger)
    {
        _settingsService = settingsService;
        _stalenessStore = stalenessStore;
        _logger = logger;

        // Settings are already loaded and cached by the time any handler is constructed
        // (App startup awaits GetSettingsAsync before showing any window), so this sync
        // wait returns immediately from the in-memory cache.
        try
        {
            var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
            _toolsEnabled = settings.AssistantFileToolsEnabled;
            UpdateFolder(settings.AssistantFilesFolder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load initial AssistantFilesFolder");
        }

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// True when the file tools are enabled. Used by the plugin host to gate tool registration, the
    /// system prompt, and the route table. The folder is always set in prod (the vault lives under it),
    /// and an unattended run supplies its own <see cref="TaskContext.WorkspaceRoot"/>, so this no longer
    /// requires a configured interactive folder — otherwise a granted headless write would never route
    /// (the route table stays empty while no folder is set). The per-call guard in
    /// <see cref="HandleToolCallAsync"/> is the backstop for a genuinely-missing folder (§17.3).
    /// </summary>
    public bool IsAvailable => _toolsEnabled;

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // The sandbox folder can be re-pointed (or cleared) at runtime. Evict the staleness
        // store so a read recorded under the old root can't satisfy a staleness check for a
        // re-pointed path, and so entries don't accumulate across the session lifetime (§0.2).
        _stalenessStore.Clear();
        _toolsEnabled = settings.AssistantFileToolsEnabled;
        UpdateFolder(settings.AssistantFilesFolder);
    }

    private void UpdateFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            _currentFolder = null;
            return;
        }
        try
        {
            var full = Path.GetFullPath(folder);
            // Canonicalize so a junction/symlink in the configured root path itself is not a
            // sandbox hole. Canonicalize requires an existing handle; preserve the prior
            // behavior (IsAvailable stays true for a not-yet-created folder — the per-call
            // Directory.Exists guard handles the missing case) by only canonicalizing when it exists.
            _currentFolder = Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : full;
        }
        catch { _currentFolder = null; }
    }

    public IList<AITool> GetTools()
    {
        if (!IsAvailable) return [];

        return
        [
            AIFunctionFactory.Create(ListFilesSchema, "list_files",
                "List text files inside the user's assistant files folder. Returns relative paths the other file tools accept. Ignored paths (built-in defaults such as .git/bin/obj/node_modules, plus any .gitignore/.piaignore entries in the folder) are omitted."),

            AIFunctionFactory.Create(FindFilesSchema, "find_files",
                "Locate files by name or path glob inside the assistant files folder, e.g. '*.md' or 'docs/**/*.md'. Read-only; returns relative paths the other file tools accept. Use this for a filename lookup — search_files scans file CONTENT and is the wrong tool for finding a file by name."),

            AIFunctionFactory.Create(ReadFileSchema, "read_file",
                "Read the contents of a text file inside the assistant files folder. Use this before summarizing or updating a file. " +
                "Locate the file with find_files first if you do not already know its path. " +
                "Also reads .docx (one line per paragraph), .xlsx/.xlsm (each sheet as a '## Sheet: name' header followed by " +
                "tab-separated rows) and .msg/.eml (headers, then the message body) as plain text — .docm and other " +
                "macro-enabled Word/template variants are not supported."),

            AIFunctionFactory.Create(WriteFileSchema, "write_file",
                "Create or overwrite a text file inside the assistant files folder. Used for both creating new files and updating existing ones. " +
                "'content' is raw file text: read_file's 'N|' line-number prefixes and its 'total_lines=' header are display only — strip them. " +
                "For .docx/.xlsx, pass the FULL updated text, same lines in the same order read_file returned them, with only the parts you're changing edited — " +
                "only the paragraphs/cells whose text differs from the current file are touched, so everything else (styles, formulas, images, " +
                "other sheets) survives untouched. Editing a paragraph's text collapses its internal formatting to a single run. New xlsx rows " +
                "can only be appended at a sheet's end (no mid-sheet insert or row deletion); a sheet you omit from the new content is left " +
                "untouched, not deleted. Macro-enabled/template files (.docm/.xlsm/.dotm/.xltm/.dotx/.xltx) can't be written."),

            AIFunctionFactory.Create(EditFileSchema, "edit_file",
                "Replace an exact piece of text in an existing file — the preferred way to change part of a file. " +
                "Prefer this over write_file for any edit to an existing file: write_file needs the WHOLE file back, " +
                "and re-typing a long document risks corrupting the parts you did not mean to change. " +
                "'old_string' must match the current file exactly (as read_file shows it, without the 'N|' prefix) and " +
                "must be unique unless 'replace_all' is true. Works for .docx/.xlsx too — only the paragraphs/cells " +
                "whose text actually changes are touched. Shows the user the same diff preview and needs their approval."),

            AIFunctionFactory.Create(DeleteFileSchema, "delete_file",
                "Delete a file inside the assistant files folder."),

            AIFunctionFactory.Create(SearchFilesSchema, "search_files",
                "Scan the CONTENT of files inside the assistant files folder for a regular-expression pattern — text files, and .docx/.xlsx/.msg/.eml as the same extracted text read_file returns, so a hit's line number is the read_file line number. Read-only; returns matching lines, matching file paths, or a count. Narrow it with 'include' to a file glob. To find a file by its name or path instead, use find_files.")
        ];
    }

    public async Task<(object? Result, FilesToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("FilesToolHandler dispatching: {ToolName}", toolCall.Name);
#if DEBUG
        Debug.WriteLine($"[FilesToolHandler Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        // An unattended run supplies its own isolated workspace root (§17.2/G-1): resolve every
        // file operation against it instead of the interactive folder, so all containment rejections
        // re-anchor to runs\<runId>. Null (the interactive path) keeps the configured folder.
        var ambientRoot = TaskAmbient.Current?.WorkspaceRoot;
        var baseRoot = ambientRoot is not null ? SafeFolderPath.NormalizeWorkspaceRoot(ambientRoot) : _currentFolder;
        if (baseRoot is null || !Directory.Exists(baseRoot))
        {
            return (
                "Error: No assistant files folder is configured. Ask the user to set one under Settings → Assistant.",
                null);
        }

        // Narrow the sandbox to the active chat's working directory (if any). The deferred
        // write closure captures this resolved root at prepare time, so it never reads the
        // ambient after the approval await.
        var root = ResolveEffectiveRoot(baseRoot, TaskAmbient.Current?.WorkingSubpath);

        return toolCall.Name switch
        {
            "list_files" => (HandleListFiles(root, args), null),
            "find_files" => (HandleFindFiles(root, args), null),
            "read_file"  => (await HandleReadFileAsync(root, args, cancellationToken), null),
            "write_file" => await PrepareWriteFileAsync(root, args, cancellationToken),
            "edit_file" => await PrepareEditFileAsync(root, args, cancellationToken),
            "delete_file" => PrepareDeleteFile(root, args),
            "search_files" => (await HandleSearchFilesAsync(root, args, cancellationToken), null),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (FilesToolCall?)null)
        };
    }

    /// <summary>
    /// Narrows the sandbox to the active chat's working subpath. A null/whitespace subpath
    /// (the default) resolves to <paramref name="baseRoot"/>. Otherwise the subpath is resolved
    /// inside the base sandbox and must exist on disk; anything that escapes containment or is
    /// missing falls back to <paramref name="baseRoot"/> (fail safe — never widen beyond it).
    /// Containment narrowing is intended: a chat scoped to a subfolder resolves relative paths
    /// under it and rejects absolute paths elsewhere in the sandbox.
    /// </summary>
    internal string ResolveEffectiveRoot(string baseRoot, string? workingSubpath)
    {
        if (string.IsNullOrWhiteSpace(workingSubpath))
            return baseRoot;

        if (SafeFolderPath.TryResolveInsideAllowingAbsolute(baseRoot, workingSubpath, out var eff)
            && Directory.Exists(eff))
        {
            return eff;
        }

        _logger.SensitiveDebug("Working subpath did not resolve to an existing folder under the sandbox: {Subpath}", workingSubpath);
        return baseRoot;
    }

    public string? DescribeEffectiveRoot(string? workingSubpath)
    {
        // Nothing to describe once the user switches the tools off — GetTools() is empty too.
        if (!IsAvailable) return null;

        // Deliberately the configured folder, not the ambient workspace root: a caller that has an
        // ambient root describes that root itself.
        var baseRoot = _currentFolder;
        if (baseRoot is null || !Directory.Exists(baseRoot)) return null;

        return ResolveEffectiveRoot(baseRoot, workingSubpath);
    }

    public async Task<object?> ExecutePendingActionAsync(FilesToolCall pendingAction)
    {
        _logger.LogDebug("Executing files action: {ToolName}", pendingAction.ToolName);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Files action completed: {ToolName}", pendingAction.ToolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute files tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private object HandleListFiles(string root, IDictionary<string, object?> args)
    {
        var pattern = GetOptionalStringArg(args, "pattern");

        // The pattern is a file-name glob applied per directory during the walk. A path-bearing
        // pattern (e.g. "docs/*.md") would make the per-directory enumeration throw where the
        // component is absent AND could re-enter an ignore-pruned directory (e.g. "bin/*.dll"),
        // so reject it with guidance rather than silently mis-listing.
        if (!string.IsNullOrEmpty(pattern) && (pattern.Contains('/') || pattern.Contains('\\')))
            return "Error: 'pattern' must be a file-name glob (e.g. '*.md' or 'notes*'), not a path. " +
                   "Omit it to list all files, or use search_files with a 'path' to scope a subdirectory.";

        List<string> rels;
        try
        {
            rels = CollectRelativeFiles(
                root,
                string.IsNullOrWhiteSpace(pattern) ? "*" : pattern!,
                MaxListEntries,
                SandboxIgnore.ForRoot(root));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate files in sandbox");
            return $"Error: Could not list files ({ex.Message}).";
        }

        if (rels.Count == 0) return "No files found in the assistant files folder.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {rels.Count} file(s) (relative paths):");
        foreach (var r in rels) sb.AppendLine($"  {r}");
        if (rels.Count == MaxListEntries) sb.AppendLine($"  ... (truncated at {MaxListEntries})");
        return sb.ToString();
    }

    /// <summary>
    /// Shared sandbox file enumeration for <c>list_files</c> and the <c>@Files</c> picker. Walks
    /// <paramref name="root"/> depth-first, pruning directories matched by <paramref name="ignore"/>
    /// BEFORE descending — so <c>.git</c>/<c>bin</c>/<c>obj</c>/<c>node_modules</c> (and any user-
    /// ignored tree) never consume the listing cap or the walk budget — and applies the filtering both
    /// consumers must agree on: canonical-containment (discard anything that resolves outside root via
    /// junction/symlink), the sensitive-path blocklist, and the ignore matcher on files. Returns
    /// sandbox-relative paths (native separators, derived from the enumerated path) capped at
    /// <paramref name="max"/>. Throws on enumeration failure (e.g. an invalid glob pattern) — callers
    /// translate that into their own error surface.
    /// </summary>
    private static List<string> CollectRelativeFiles(string root, string searchPattern, int max, GitignoreMatcher ignore)
    {
        var rels = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Prune ignored directories before descending (skips the whole subtree) and discard
                // anything that, after junction/symlink resolution, escapes root.
                var relDir = NormalizeSeparators(SafeRelative(root, sub));
                if (ignore.IsIgnored(relDir, isDirectory: true)) continue;
                // A run's own working notes. Unconditional here: list_files takes no path, so there is no
                // request to carve out for — read_file and write_file on an explicit path still reach it.
                if (RunScratchFolder.Contains(relDir)) continue;
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, sub, out _)) continue;
                stack.Push(sub);
            }

            foreach (var full in Directory.EnumerateFiles(dir, searchPattern))
            {
                // Canonicalizing safety net: discard anything that, after junction/symlink
                // resolution, isn't inside root. (Supersedes the old lexical StartsWith net.)
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out var canon)) continue;
                // Don't list protected system/app-data files even inside a broad sandbox (symmetric
                // with read/search/write/delete — the blocklist applies regardless of sandbox scope).
                if (SensitivePathGuard.IsBlocked(canon, out _)) continue;
                // Display path is derived from the (already lexically-under-root) enumerated path.
                var rel = SafeRelative(root, full);
                if (ignore.IsIgnored(NormalizeSeparators(rel), isDirectory: false)) continue;
                rels.Add(rel);
                if (rels.Count >= max) return rels;
            }
        }
        return rels;
    }

    public IReadOnlyList<string> ListRelativeFiles(string? filter, int max)
    {
        if (max <= 0) return [];

        var baseRoot = _currentFolder;
        if (baseRoot is null || !Directory.Exists(baseRoot)) return [];

        // Scope @Files autocomplete to the active chat's working dir. This runs OUTSIDE any
        // turn, so TaskAmbient is empty; the active subpath is pushed in by the view model.
        var root = ResolveEffectiveRoot(baseRoot, ActiveUiWorkingSubpath);

        List<string> all;
        try
        {
            // Collect the full (capped) listing first, then filter — capping before the
            // substring filter would only ever search the first N enumerated files.
            all = CollectRelativeFiles(root, "*", MaxListEntries, SandboxIgnore.ForRoot(root));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate files for @Files autocomplete");
            return [];
        }

        IEnumerable<string> query = all.Select(NormalizeSeparators);
        if (!string.IsNullOrWhiteSpace(filter))
            query = query.Where(r => r.Contains(filter, StringComparison.OrdinalIgnoreCase));

        return query
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToArray();
    }

    /// <summary>
    /// Normalizes a sandbox-relative path to forward slashes. The file tools resolve a
    /// path via <see cref="Path.GetFullPath(string, string)"/>, which accepts <c>/</c> on
    /// Windows, so a forward-slash path stays inside the sandbox while avoiding the
    /// backslash-escape corruption that occurs when the model copies a path into a JSON
    /// tool argument (e.g. <c>notes\there.md</c> → a stray <c>\t</c>).
    /// </summary>
    private static string NormalizeSeparators(string relativePath)
        => relativePath.Replace('\\', '/');

    // The lookahead is what keeps "/c" from matching inside "/config/x".
    private static readonly Regex[] PosixDrivePrefixes =
    [
        new(@"^/(?:cygdrive|mnt)/([a-zA-Z])(?=/|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^/([a-zA-Z]):(?=/|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^/([a-zA-Z])(?=/|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant)
    ];

    /// <summary>Rewrites the POSIX drive spellings a model trained on Unix shells emits as <c>C:/x</c>.</summary>
    internal static string NormalizePathArg(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        foreach (var prefix in PosixDrivePrefixes)
        {
            var match = prefix.Match(path);
            if (!match.Success) continue;
            var drive = char.ToUpperInvariant(match.Groups[1].Value[0]);
            return $"{drive}:/{path[match.Length..].TrimStart('/')}";
        }
        return path;
    }

    /// <summary>
    /// Its own walk rather than <see cref="CollectRelativeFiles"/>, whose per-directory name filter
    /// cannot express a path-bearing pattern.
    /// </summary>
    private object HandleFindFiles(string root, IDictionary<string, object?> args)
    {
        var pattern = GetStringArg(args, "pattern");
        if (string.IsNullOrEmpty(pattern))
            return "Error: A 'pattern' (file glob) is required.";

        var limit = Math.Clamp(GetOptionalIntArg(args, "limit", DefaultFindLimit), 1, MaxFindLimit);
        var ignore = SandboxIgnore.ForRoot(root);

        var requestedPath = GetOptionalStringArg(args, "path") is { } posixPath ? NormalizePathArg(posixPath) : null;
        string searchRoot;
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath == ".")
        {
            searchRoot = root;
        }
        else if (SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requestedPath, out var resolvedDir)
                 && Directory.Exists(resolvedDir))
        {
            searchRoot = resolvedDir;
        }
        else
        {
            _logger.LogInformation("find_files path not found under sandbox");
            _logger.SensitiveDebug("find_files unresolved path: {Path}", requestedPath);
            var suggestions = SuggestSimilarDirectories(root, requestedPath!, ignore);
            var msg = $"Error: Path '{requestedPath}' was not found inside the assistant files folder.";
            return suggestions.Count > 0
                ? msg + " Did you mean: " + string.Join(", ", suggestions) + "?"
                : msg;
        }

        Regex glob;
        try
        {
            glob = GlobPattern.Compile(pattern);
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("find_files rejected invalid pattern");
            _logger.SensitiveDebug("find_files invalid pattern {Pattern}: {Error}", pattern, ex.Message);
            return $"Error: Invalid glob pattern: {ex.Message}";
        }

        var canonRootWithSep = SafeFolderPath.WithTrailingSeparator(root);
        var hits = new List<string>();
        int filesScanned = 0;
        bool scanTruncated = false;

        var stack = new Stack<string>();
        stack.Push(searchRoot);

        // The carve-out: a search the caller pointed AT .scratch keeps seeing it. searchRoot is pushed
        // directly and never meets the prune, so this only has to cover its subdirectories.
        var searchingInsideScratch = RunScratchFolder.Contains(NormalizeSeparators(SafeRelative(root, searchRoot)));

        try
        {
            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch { subDirs = []; }
                foreach (var sub in subDirs)
                {
                    var relDir = NormalizeSeparators(SafeRelative(root, sub));
                    if (ignore.IsIgnored(relDir, isDirectory: true)) continue;
                    if (!searchingInsideScratch && RunScratchFolder.Contains(relDir)) continue;
                    if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, sub, out _)) continue;
                    stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { files = []; }

                foreach (var full in files)
                {
                    if (filesScanned >= MaxFilesScanned) { scanTruncated = true; break; }
                    filesScanned++;

                    if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out var canon)) continue;
                    if (!canon.StartsWith(canonRootWithSep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (SensitivePathGuard.IsBlocked(canon, out _)) continue;

                    var rel = NormalizeSeparators(SafeRelative(root, full));
                    if (ignore.IsIgnored(rel, isDirectory: false)) continue;
                    if (!glob.IsMatch(NormalizeSeparators(SafeRelative(searchRoot, full)))) continue;

                    hits.Add(rel);
                }

                if (scanTruncated) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "find_files walk failed");
            return $"Error: Search failed ({ex.Message}).";
        }

        _logger.LogInformation("find_files scanned {Files} file(s), {Matches} match(es)", filesScanned, hits.Count);
        _logger.SensitiveDebug("find_files pattern {Pattern} under {Path}", pattern, requestedPath ?? "(root)");

        var sb = new StringBuilder();
        if (scanTruncated)
            sb.Append($"Warning: stopped after scanning {MaxFilesScanned} files; results may be incomplete. Narrow the search with a 'path'.\n\n");

        if (hits.Count == 0)
        {
            sb.Append("No files found.");
            return sb.ToString();
        }

        hits.Sort(StringComparer.OrdinalIgnoreCase);

        var shown = Math.Min(limit, hits.Count);
        for (int i = 0; i < shown; i++) sb.Append(hits[i]).Append('\n');
        if (hits.Count > shown)
            sb.Append($"(Results are truncated: showing first {shown} of {hits.Count} results. Consider using a more specific path or pattern.)\n");

        return sb.ToString().TrimEnd('\n');
    }

    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Hand-rolled, in-process regex search over the sandbox. Walks the tree with a manual stack
    /// (pruning directories matched by the ignore matcher before descending so we never spend the
    /// scan budget on .git/bin/obj/node_modules or the user's ignored trees), reads each file through
    /// <see cref="ReadFileTextAsync"/> so .docx/.xlsx/.msg/.eml are searched as extracted text rather
    /// than skipped as binary, matches each line against the user regex (guarded by a match timeout
    /// against catastrophic backtracking), and emits a diagnostics block followed by a results body.
    /// Read-only: the caller wraps the return as (result, null) — no action card.
    /// </summary>
    private async Task<object> HandleSearchFilesAsync(
        string root, IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var pattern = GetStringArg(args, "pattern");
        if (string.IsNullOrEmpty(pattern))
            return "Error: A 'pattern' (regular expression) is required.";

        var mode = NormalizeSearchMode(GetOptionalStringArg(args, "mode"));
        var offset = Math.Max(1, GetOptionalIntArg(args, "offset", 1));
        var limit = Math.Clamp(GetOptionalIntArg(args, "limit", 100), 1, MaxMatches);

        // Ignore matcher for this sandbox root (shipped defaults + folder .gitignore/.piaignore). Dir
        // paths are matched relative to root, so the same matcher works from a scoped searchRoot below.
        var ignore = SandboxIgnore.ForRoot(root);

        // Resolve the search root. A missing/empty/"." path means the whole sandbox; the
        // permissive resolver rejects the root itself, so special-case it rather than route it.
        var requestedPath = GetOptionalStringArg(args, "path") is { } posixPath ? NormalizePathArg(posixPath) : null;
        string searchRoot;
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath == ".")
        {
            searchRoot = root;
        }
        else if (SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requestedPath, out var resolvedDir)
                 && Directory.Exists(resolvedDir))
        {
            searchRoot = resolvedDir;
        }
        else
        {
            // Path-not-found: offer similar-name suggestions (best effort) instead of crashing.
            _logger.LogInformation("search_files path not found under sandbox");
            _logger.SensitiveDebug("search_files unresolved path: {Path}", requestedPath);
            var suggestions = SuggestSimilarDirectories(root, requestedPath!, ignore);
            var msg = $"Error: Path '{requestedPath}' was not found inside the assistant files folder.";
            return suggestions.Count > 0
                ? msg + " Did you mean: " + string.Join(", ", suggestions) + "?"
                : msg;
        }

        Regex regex;
        try
        {
            // Case-insensitive unless asked otherwise. Inferring intent from the pattern's own
            // capitals (ripgrep's smart case) reads a developer's deliberate SHOUTING into what is
            // just orthography here — a model writes "Cookie" or a German noun capitalized without
            // meaning "exact", and would silently miss the lowercase occurrences.
            var caseOption = GetBoolArg(args, "case_sensitive") ? RegexOptions.None : RegexOptions.IgnoreCase;
            regex = new Regex(pattern, caseOption, SearchRegexTimeout);
        }
        catch (ArgumentException ex)
        {
            // Invalid regex is a diagnostic, not a crash.
            _logger.LogInformation("search_files rejected invalid regex");
            _logger.SensitiveDebug("search_files invalid pattern {Pattern}: {Error}", pattern, ex.Message);
            return $"Error: Invalid regular expression: {ex.Message}";
        }

        var include = GetOptionalStringArg(args, "include");
        Regex? includeGlob = null;
        if (!string.IsNullOrWhiteSpace(include))
        {
            try { includeGlob = GlobPattern.Compile(include!); }
            catch (ArgumentException ex)
            {
                _logger.LogInformation("search_files rejected invalid include glob");
                _logger.SensitiveDebug("search_files invalid include {Include}: {Error}", include, ex.Message);
                return $"Error: Invalid 'include' glob: {ex.Message}";
            }
        }

        var diagnostics = new List<string>();
        // A newline in the pattern won't match anything (we search line by line) — warn explicitly.
        if (pattern.Contains('\n') || pattern.Contains("\\n", StringComparison.Ordinal))
            diagnostics.Add("Warning: the pattern contains a newline; search matches one line at a time, so multiline patterns will not match.");

        var canonRootWithSep = SafeFolderPath.WithTrailingSeparator(root);

        // Collected results. For content mode each entry is one matching line; for files/count
        // mode each entry is one file (with an optional count).
        var matches = new List<(string Rel, int Line, string Text, int Count)>();
        int filesScanned = 0;
        int filesSearched = 0;
        int extractions = 0;
        int skippedCount = 0;
        var skippedNames = new List<string>();
        bool scanTruncated = false;
        bool matchTruncated = false;
        bool extractionTruncated = false;
        bool regexTimedOut = false;

        // Named, not just counted: "3 PDFs I can't search" is actionable, "0 matches" is misleading.
        void RecordSkipped(string rel)
        {
            skippedCount++;
            if (skippedNames.Count < SkippedNamesShown) skippedNames.Add(rel);
        }

        var stack = new Stack<string>();
        stack.Push(searchRoot);

        // The carve-out: a search the caller pointed AT .scratch keeps seeing it. searchRoot is pushed
        // directly and never meets the prune, so this only has to cover its subdirectories.
        var searchingInsideScratch = RunScratchFolder.Contains(NormalizeSeparators(SafeRelative(root, searchRoot)));

        try
        {
            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch { subDirs = []; }
                foreach (var sub in subDirs)
                {
                    // Prune by ignore pattern (matched against the root-relative path), not substring.
                    var relDir = NormalizeSeparators(SafeRelative(root, sub));
                    if (ignore.IsIgnored(relDir, isDirectory: true)) continue;
                    // A run's own scratch is not evidence about the user's folder — BG3 reported a TODO it
                    // had written into its own notes as a finding about the config files.
                    if (!searchingInsideScratch && RunScratchFolder.Contains(relDir)) continue;
                    // Containment net on directories too (parity with CollectRelativeFiles) — don't
                    // descend a junction/symlink that resolves outside the sandbox base.
                    if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, sub, out _)) continue;
                    stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { files = []; }

                foreach (var full in files)
                {
                    // The extraction readers swallow cancellation into a Failed result, so without this
                    // a cancelled search would walk the whole tree marking every file as skipped.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (filesScanned >= MaxFilesScanned) { scanTruncated = true; break; }
                    // Counted before the filters on purpose: MaxFilesScanned bounds the walk itself, so a
                    // narrow include over a huge tree still trips the guard.
                    filesScanned++;

                    // Defense in depth: re-filter every path through the canonicalized root prefix,
                    // discarding anything that (after junction/symlink resolution) escapes the base.
                    if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out var canon)) continue;
                    if (!canon.StartsWith(canonRootWithSep, StringComparison.OrdinalIgnoreCase)) continue;
                    // Don't surface contents of protected system/app-data files even inside a broad sandbox.
                    if (SensitivePathGuard.IsBlocked(canon, out _)) continue;
                    // Honor file-level ignore patterns so search agrees with list_files/@Files on which
                    // files exist (e.g. a user's ".piaignore" "secret.txt" or the default "*.log" is not
                    // scanned and its contents are not surfaced). Directory-level pruning above only
                    // skips ignored trees; a file-granular rule must be applied here too.
                    if (ignore.IsIgnored(NormalizeSeparators(SafeRelative(root, full)), isDirectory: false)) continue;
                    // Before any IO, so an excluded file costs nothing. Matched against the SEARCHED
                    // folder so the glob reads naturally against the folder the caller pointed at.
                    if (includeGlob is not null &&
                        !includeGlob.IsMatch(NormalizeSeparators(SafeRelative(searchRoot, full)))) continue;

                    // Emit a SANDBOX-ROOT-relative path (the same form list_files/read_file accept) so a
                    // scoped hit round-trips: read_file resolves both the path and the line number.
                    var rel = NormalizeSeparators(SafeRelative(root, full));

                    // Opening an OpenXml/mail container costs orders of magnitude more than a byte
                    // read, so extraction gets a budget of its own.
                    bool extracts = IsExtractedKind(DroppedFileReader.Classify(canon));
                    if (extracts && extractions >= MaxExtractions) { extractionTruncated = true; continue; }
                    if (extracts) extractions++;

                    string[] lines;
                    try
                    {
                        // Same routing read_file uses, so .docx/.xlsx/.msg/.eml are searched as extracted
                        // text and the size/binary/image guards stay in one place.
                        var (text, readError) = await ReadFileTextAsync(canon, rel, cancellationToken);
                        if (readError is not null) { RecordSkipped(rel); continue; }
                        lines = text!.Split('\n');
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { RecordSkipped(rel); continue; }

                    filesSearched++;

                    int fileMatchCount = 0;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        if (line.EndsWith('\r')) line = line[..^1];

                        bool isMatch;
                        try { isMatch = regex.IsMatch(line); }
                        catch (RegexMatchTimeoutException)
                        {
                            regexTimedOut = true;
                            isMatch = false;
                        }
                        if (regexTimedOut) break;
                        if (!isMatch) continue;

                        fileMatchCount++;
                        if (mode == "content")
                        {
                            matches.Add((rel, i + 1, line, 0));
                            if (matches.Count >= MaxMatches) { matchTruncated = true; break; }
                        }
                    }

                    if (regexTimedOut) break;

                    if (mode != "content" && fileMatchCount > 0)
                    {
                        matches.Add((rel, 0, string.Empty, fileMatchCount));
                        if (matches.Count >= MaxMatches) { matchTruncated = true; }
                    }

                    if (matchTruncated) break;
                }

                if (scanTruncated || matchTruncated || regexTimedOut) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_files walk failed");
            return $"Error: Search failed ({ex.Message}).";
        }

        if (regexTimedOut)
            diagnostics.Add($"Warning: the pattern took too long to evaluate (over {SearchRegexTimeout.TotalSeconds:0}s) and was stopped; results may be incomplete. Simplify the regular expression.");
        if (scanTruncated)
            diagnostics.Add($"Warning: stopped after scanning {MaxFilesScanned} files; results may be incomplete. Narrow the search with a 'path'.");
        if (extractionTruncated)
            diagnostics.Add($"Warning: stopped extracting .docx/.xlsx/.msg/.eml after {MaxExtractions} file(s); the rest were not searched. Narrow the search with a 'path' or 'include'.");
        if (skippedCount > 0)
            diagnostics.Add(
                $"Note: {skippedCount} file(s) could not be searched (binary, image, or over the size limit): " +
                string.Join(", ", skippedNames) + (skippedCount > skippedNames.Count ? ", …" : "") + ".");
        if (matchTruncated)
            diagnostics.Add($"Note: more than {MaxMatches} matches; collection stopped at {MaxMatches} (truncated at {MaxMatches}).");

        _logger.LogInformation(
            "search_files scanned {Files} file(s), searched {Searched}, extracted {Extracted}, skipped {Skipped}, {Matches} match(es), mode {Mode}",
            filesScanned, filesSearched, extractions, skippedCount, matches.Count, mode);
        _logger.SensitiveDebug("search_files pattern {Pattern} under {Path}", pattern, requestedPath ?? "(root)");

        return FormatSearchResults(matches, mode, offset, limit, diagnostics);
    }

    private static string NormalizeSearchMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "content";
        var m = mode.Trim().ToLowerInvariant();
        return m switch
        {
            "files" or "files-only" or "files_only" or "paths" => "files",
            "count" or "counts" => "count",
            _ => "content"
        };
    }

    /// <summary>
    /// Renders the diagnostics block (warnings/notes, kept separate from results) followed by the
    /// windowed result body with a truncation hint when more results remain than the window shows.
    /// </summary>
    private static string FormatSearchResults(
        List<(string Rel, int Line, string Text, int Count)> matches,
        string mode, int offset, int limit, List<string> diagnostics)
    {
        var sb = new StringBuilder();

        // Diagnostics first, clearly separated from results.
        if (diagnostics.Count > 0)
        {
            foreach (var d in diagnostics) sb.Append(d).Append('\n');
            sb.Append('\n');
        }

        int total = matches.Count;
        if (total == 0)
        {
            sb.Append("No matches found.");
            return sb.ToString();
        }

        int startIdx = offset - 1; // 0-indexed
        if (startIdx >= total)
        {
            sb.Append($"(no results: offset {offset} is past the last result; total={total})");
            return sb.ToString();
        }

        int endIdx = Math.Min(startIdx + limit, total);
        var window = matches.GetRange(startIdx, endIdx - startIdx);

        switch (mode)
        {
            case "files":
                sb.Append($"matches={total} (files)\n");
                foreach (var m in window) sb.Append(m.Rel).Append('\n');
                break;
            case "count":
                sb.Append($"matches={total} (files with counts)\n");
                foreach (var m in window) sb.Append(m.Rel).Append(": ").Append(m.Count).Append('\n');
                break;
            default: // content
                sb.Append($"matches={total}\n");
                foreach (var m in window) sb.Append(m.Rel).Append(':').Append(m.Line).Append(':').Append(m.Text).Append('\n');
                break;
        }

        if (endIdx < total)
            sb.Append($"... (showing {endIdx - startIdx} of {total}; pass offset={endIdx + 1})").Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Best-effort similar-name suggestions for a not-found <paramref name="requestedPath"/>: returns
    /// up to three sandbox subdirectory names sharing a substring with the leaf name (edit-distance
    /// fallback when none does), emitted root-relative with forward slashes. Walks with a manual stack
    /// so directories matched by <paramref name="ignore"/> are pruned before descending — a suggestion
    /// must never point at an ignored tree.
    /// </summary>
    private static List<string> SuggestSimilarDirectories(string root, string requestedPath, GitignoreMatcher ignore)
    {
        var leaf = Path.GetFileName(requestedPath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf)) return [];

        var rootWithSep = SafeFolderPath.WithTrailingSeparator(root);
        var hits = new List<string>();
        var fuzzy = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        try
        {
            while (stack.Count > 0 && hits.Count < 3)
            {
                var dir = stack.Pop();
                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch { subDirs = []; }

                foreach (var sub in subDirs)
                {
                    var relDir = NormalizeSeparators(SafeRelative(root, sub));
                    if (ignore.IsIgnored(relDir, isDirectory: true)) continue; // don't descend/suggest ignored trees
                    if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, sub, out _)) continue; // stay inside the sandbox
                    stack.Push(sub);

                    var name = Path.GetFileName(sub);
                    var contains = name.Contains(leaf, StringComparison.OrdinalIgnoreCase) ||
                                   leaf.Contains(name, StringComparison.OrdinalIgnoreCase);
                    if (!contains && (fuzzy.Count >= 3 || !IsFuzzyNameMatch(name, leaf))) continue;

                    var rel = sub.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                        ? NormalizeSeparators(sub.Substring(rootWithSep.Length)) : name;
                    if (contains)
                    {
                        hits.Add(rel);
                        if (hits.Count >= 3) break;
                    }
                    else
                    {
                        fuzzy.Add(rel);
                    }
                }
            }
        }
        catch { /* best effort */ }
        return hits.Count > 0 ? hits : fuzzy;
    }

    /// <summary>
    /// Best-effort similar-name suggestions for a read_file miss: up to three sibling files whose
    /// name shares a substring with the requested one (edit-distance fallback when none does),
    /// emitted root-relative with forward slashes.
    /// </summary>
    private static List<string> SuggestSimilarFiles(string root, string requestedFullPath, GitignoreMatcher ignore)
    {
        var hits = new List<string>();
        var fuzzy = new List<string>();
        try
        {
            var parent = Path.GetDirectoryName(requestedFullPath);
            var leaf = Path.GetFileName(requestedFullPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf) || !Directory.Exists(parent))
                return hits;

            // This enumerates the parent directly instead of walking down to it, so a directory-only
            // rule like "secret/" never got its chance to prune — re-check every ancestor or the names
            // inside an ignored folder leak.
            if (IsUnderIgnoredDirectory(root, parent, ignore)) return hits;

            foreach (var full in Directory.EnumerateFiles(parent))
            {
                var name = Path.GetFileName(full);
                var contains = name.Contains(leaf, StringComparison.OrdinalIgnoreCase) ||
                               leaf.Contains(name, StringComparison.OrdinalIgnoreCase);
                if (!contains && (fuzzy.Count >= 3 || !IsFuzzyNameMatch(name, leaf))) continue;
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out var canon)) continue;
                if (SensitivePathGuard.IsBlocked(canon, out _)) continue;

                var rel = NormalizeSeparators(SafeRelative(root, full));
                if (ignore.IsIgnored(rel, isDirectory: false)) continue;

                if (contains)
                {
                    hits.Add(rel);
                    if (hits.Count >= 3) break;
                }
                else
                {
                    fuzzy.Add(rel);
                }
            }
        }
        catch { /* best effort */ }
        return hits.Count > 0 ? hits : fuzzy;
    }

    private static bool IsUnderIgnoredDirectory(string root, string directory, GitignoreMatcher ignore)
    {
        var rootWithSep = SafeFolderPath.WithTrailingSeparator(root);
        var dir = directory;
        while (!string.IsNullOrEmpty(dir) && dir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            if (ignore.IsIgnored(NormalizeSeparators(SafeRelative(root, dir)), isDirectory: true)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    // Substring matches stay the first choice; this only fires when none exist. OSA variant so a
    // transposition costs 1 like the other one-keystroke typos; the threshold scales with length.
    private static bool IsFuzzyNameMatch(string name, string leaf)
        => EditDistance(name, leaf) <= Math.Max(1, leaf.Length / 4);

    private static int EditDistance(string a, string b)
    {
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 &&
                    char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2]) &&
                    char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    cur[j] = Math.Min(cur[j], prev2[j - 2] + 1);
                }
            }
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }

    private async Task<object> HandleReadFileAsync(
        string root, IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var requested = NormalizePathArg(GetStringArg(args, "path"));
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
        {
            _logger.LogWarning("read_file rejected path outside sandbox");
            _logger.SensitiveDebug("read_file rejected path: {Path}", requested);
            return "Error: Path is outside the assistant files folder.";
        }

        // SENSITIVE-PATH BLOCKLIST: even read-only access to Pia's own DB/config or system/credential
        // dirs is an exfiltration vector when the sandbox is configured broadly, so block the same
        // protected roots write_file/delete_file reject (independent of how the sandbox is scoped).
        if (SensitivePathGuard.IsBlocked(safePath, out var blockReason))
        {
            _logger.LogWarning("read_file rejected: sensitive path");
            _logger.SensitiveDebug("read_file blocked path: {Path}", safePath);
            return $"Error: Refusing to read here — {blockReason}.";
        }

        if (!File.Exists(safePath))
        {
            var suggestions = SuggestSimilarFiles(root, safePath, SandboxIgnore.ForRoot(root));
            var notFound = $"Error: File '{requested}' not found.";
            return suggestions.Count > 0
                ? notFound + " Did you mean: " + string.Join(", ", suggestions) + "?"
                : notFound;
        }

        // offset is 1-indexed (default 1, min 1); limit defaults to 500, clamped to [1, 2000].
        var offset = Math.Max(1, GetOptionalIntArg(args, "offset", 1));
        var limit = Math.Clamp(GetOptionalIntArg(args, "limit", DefaultReadLimit), 1, MaxReadLimit);
        var windowRequested = args.ContainsKey("offset") || args.ContainsKey("limit");

        try
        {
            var (text, readError) = await ReadFileTextAsync(safePath, requested, cancellationToken);
            if (readError is not null)
                return readError;

            var result = FormatWindow(text!, offset, limit, windowRequested);

            // Record the observed mtime keyed by the canonicalized resolved path (not the model string).
            _stalenessStore.RecordRead(TaskAmbient.Current?.TaskId ?? Guid.Empty, safePath, File.GetLastWriteTimeUtc(safePath));

            // Surface the read to the active turn's message as an "open file" chip (read scope).
            TaskAmbient.Current?.OnFileTouched?.Invoke(new FileTouch(safePath, FileTouchKind.Read));

            _logger.LogInformation("read_file succeeded (offset {Offset}, limit {Limit})", offset, limit);
            _logger.SensitiveDebug("read_file path: {Path}", requested);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file");
            return $"Error: Could not read file ({ex.Message}).";
        }
    }

    /// <summary>
    /// Resolves the raw text of a sandbox file, routing by extension FIRST (structured docs are zip
    /// containers full of NUL bytes — sniffing them as binary would falsely reject them; NUL-sniff
    /// only on the plain-text path). Returns <c>(text, null)</c> on success or <c>(null, error)</c>
    /// for an oversized/binary/unsupported file. Shared by <see cref="HandleReadFileAsync"/> (which
    /// then windows + records staleness), <see cref="HandleSearchFilesAsync"/> and
    /// <see cref="ReadPromptPreviewAsync"/> (which caps for the prompt) — it does NOT record
    /// staleness, so a search hit can never satisfy the read-before-edit gate.
    /// <paramref name="requested"/> is the model-supplied path, used only in error text.
    /// </summary>
    private async Task<(string? Text, string? Error)> ReadFileTextAsync(
        string safePath, string requested, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(safePath);

        var kind = DroppedFileReader.Classify(safePath);
        if (IsExtractedKind(kind))
        {
            var extracted = kind switch
            {
                FileKind.Docx => await DroppedFileReader.ReadDocxAsync(safePath, cancellationToken),
                FileKind.Xlsx => await DroppedFileReader.ReadXlsxAsync(safePath, cancellationToken),
                _             => await DroppedFileReader.ReadEmailAsync(safePath, cancellationToken),
            };
            if (extracted.Status == DroppedFileReader.ReadStatus.TooLarge)
                return (null, $"Error: File is too large to read (max {MaxReadFileBytes / 1024} KB of extracted text).");
            if (extracted.Status == DroppedFileReader.ReadStatus.Failed)
                return (null, $"Error: Could not read file ({extracted.Error}).");
            return (extracted.Text ?? string.Empty, null);
        }

        if (IsImageExtension(ext))
            return (null, $"Error: '{requested}' is an unsupported binary file (image); attach the image instead.");

        var info = new FileInfo(safePath);
        if (info.Length > MaxReadFileBytes)
            return (null, $"Error: File is too large to read ({info.Length} bytes, max {MaxReadFileBytes} bytes). " +
                          "offset/limit cannot help here — the whole file exceeds the raw-byte ceiling.");

        var bytes = await File.ReadAllBytesAsync(safePath, cancellationToken);
        if (LooksBinary(bytes))
            return (null, $"Error: '{requested}' appears to be a binary file (contains NUL bytes) and cannot be read as text.");

        // Decode honoring a leading BOM (UTF-8/UTF-16); default to UTF-8.
        return (DecodeText(bytes), null);
    }

    // @Files prompt-injection cap regime. The line cap is the caller's policy lever; this char
    // ceiling is the safety net that bounds tokens when a file has very long lines (e.g. minified
    // code), so a 100-line preview can never blow the context.
    private const int PromptPreviewMaxChars = 12 * 1024; // ~3K-token safety net

    public async Task<FilePromptPreview> ReadPromptPreviewAsync(
        string relativePath, string? workingSubpath, int maxLines, CancellationToken cancellationToken = default)
    {
        FilePromptPreview Fail(string error) =>
            new(relativePath, Found: false, Text: null, TotalLines: 0, ShownLines: 0, Truncated: false, Error: error);

        var baseRoot = _currentFolder;
        if (baseRoot is null || !Directory.Exists(baseRoot))
            return Fail("No assistant files folder is configured.");

        // Mirror the read_file sandbox narrowing: resolve under the active chat's working dir,
        // then validate containment, the sensitive-path blocklist, and existence.
        var root = ResolveEffectiveRoot(baseRoot, workingSubpath);

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, relativePath, out var safePath))
            return Fail("Path is outside the assistant files folder.");
        if (SensitivePathGuard.IsBlocked(safePath, out var blockReason))
            return Fail($"Refusing to read here — {blockReason}.");
        if (!File.Exists(safePath))
            return Fail("File not found.");

        string? text;
        string? readError;
        try
        {
            (text, readError) = await ReadFileTextAsync(safePath, relativePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file for @Files prompt preview");
            return Fail($"Could not read file ({ex.Message}).");
        }

        if (readError is not null)
            // ReadFileTextAsync prefixes "Error: "; strip it so the preview's Error reads as a plain reason.
            return Fail(readError.StartsWith("Error: ", StringComparison.Ordinal) ? readError["Error: ".Length..] : readError);

        var (preview, total, shown, truncated) = CapForPrompt(text!, maxLines, PromptPreviewMaxChars);

        // Deliberately no _stalenessStore.RecordRead here — a preview is partial and runs during turn
        // setup (TaskAmbient is not yet this session's), so the model must still read_file before editing.
        _logger.LogInformation("@Files prompt preview ({Shown}/{Total} lines, truncated={Truncated})", shown, total, truncated);
        _logger.SensitiveDebug("@Files prompt preview path: {Path}", relativePath);
        return new FilePromptPreview(relativePath, Found: true, preview, total, shown, truncated, Error: null, AbsolutePath: safePath);
    }

    /// <summary>
    /// Caps <paramref name="text"/> to the first <paramref name="maxLines"/> lines AND
    /// <paramref name="maxChars"/> characters (whichever binds first), returning the raw content
    /// (no line-number prefixes, trailing CR stripped) plus the file's true line count and whether
    /// anything was withheld. Always emits at least the first line, even if it alone exceeds the
    /// char budget, so a single huge line is never silently dropped.
    /// </summary>
    internal static (string Text, int TotalLines, int ShownLines, bool Truncated) CapForPrompt(
        string text, int maxLines, int maxChars)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];

        // A trailing newline yields a spurious empty final element — don't count it as a line.
        int totalLines = lines.Length;
        if (totalLines > 0 && lines[^1].Length == 0 && text.EndsWith('\n'))
            totalLines--;

        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < totalLines && shown < maxLines; i++)
        {
            int addition = (shown == 0 ? 0 : 1) + lines[i].Length; // +1 for the joining '\n'
            if (shown > 0 && sb.Length + addition > maxChars) break;
            if (shown > 0) sb.Append('\n');
            sb.Append(lines[i]);
            shown++;
        }

        return (sb.ToString(), totalLines, shown, shown < totalLines);
    }

    /// <summary>
    /// Slices <paramref name="text"/> to the window <c>[offset, offset+limit)</c> (1-indexed),
    /// emits each kept line as <c>LINE|CONTENT</c> (no padding), and prefixes a header with
    /// <c>total_lines</c>. Returns narrow-window guidance (not a truncated body) when the formatted
    /// window would exceed the char/line caps; appends a pagination hint when more lines remain.
    /// </summary>
    private static string FormatWindow(string text, int offset, int limit, bool windowRequested)
    {
        // Split on LF and strip a trailing CR so CONTENT never carries \r (matters for docx/xlsx,
        // which build text with AppendLine = CRLF on Windows, and for CRLF source files).
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];

        // A trailing newline produces a spurious empty final element — drop it so total_lines is honest.
        int totalLines = lines.Length;
        if (totalLines > 0 && lines[^1].Length == 0 && text.EndsWith('\n'))
            totalLines--;

        // limit is already clamped to [1, MaxReadLimit] by the caller, so the window can never
        // exceed the 2000-line cap. Guard anyway for direct callers.
        if (limit > MaxReadLimit)
            return $"Error: Requested window is too large ({limit} lines, max {MaxReadLimit}). " +
                   $"Narrow 'limit' to {MaxReadLimit} or fewer.";

        int startIdx = offset - 1;                 // 0-indexed start
        int endIdx = Math.Min(startIdx + limit, totalLines); // exclusive

        var sb = new StringBuilder();
        sb.Append("total_lines=").Append(totalLines).Append('\n');

        if (startIdx >= totalLines)
        {
            // Out-of-range offset: empty content, correct total_lines.
            sb.Append("(no lines: offset ").Append(offset)
              .Append(" is past end of file; total_lines=").Append(totalLines).Append(')');
            return sb.ToString();
        }

        for (int i = startIdx; i < endIdx; i++)
        {
            sb.Append(i + 1).Append('|').Append(lines[i]).Append('\n');

            if (sb.Length > MaxFormattedWindowChars)
                return $"Error: The requested window is too large to return (over {MaxFormattedWindowChars / 1024}K chars). " +
                       $"Narrow the read with a smaller 'limit' or a more specific 'offset'. total_lines={totalLines}.";
        }

        // Pagination hint: window fine, but more lines remain beyond it. (With the default
        // limit of 500 this also covers the "large file read without a narrow window" case,
        // since a >500-line file always has lines past the default window.)
        if (endIdx < totalLines)
        {
            var prefix = windowRequested ? "..." : "... (large file)";
            sb.Append(prefix).Append(" (").Append(totalLines - endIdx).Append(" more line(s); use offset=")
              .Append(endIdx + 1).Append(" to continue)").Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static readonly HashSet<string> ReadImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif"
    };

    private static bool IsImageExtension(string ext)
        => !string.IsNullOrEmpty(ext) && ReadImageExtensions.Contains(ext);

    /// <summary>Formats whose text is extracted from a container rather than read as the file's own bytes.</summary>
    private static bool IsExtractedKind(FileKind kind)
        => kind is FileKind.Docx or FileKind.Xlsx or FileKind.Email;

    /// <summary>NUL-byte content sniff: a NUL in the first 8 KB strongly implies a binary file.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        int scan = Math.Min(bytes.Length, 8 * 1024);
        for (int i = 0; i < scan; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Prepares a write. Prepare-time hard failures (bad args, echo, path-outside, blocked path,
    /// oversized) are deterministic rejections with nothing to approve, so they return an immediate
    /// <c>(Result, null)</c> — no action card. Only a viable write returns a <c>(null, pending)</c>
    /// action card carrying the diff for the user to approve.
    /// </summary>
    /// <summary>
    /// Resolves an exact-string edit into the full new content, then hands it to
    /// <see cref="PrepareWriteFileAsync"/> — so the diff, the patch-engine dry run, the approval card,
    /// the atomic write and the round-trip validation are the same code, not a second copy. The point of
    /// the tool is that the model never re-types the file: it cannot mistranscribe what it does not send.
    /// </summary>
    private async Task<(object? Result, FilesToolCall? Pending)> PrepareEditFileAsync(
        string root, IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var requested = GetStringArg(args, "path");

        if (!TryGetRequiredStringArg(args, "old_string", out var oldString, out var oldArgError))
            return WriteFailure(oldArgError);
        if (!TryGetRequiredStringArg(args, "new_string", out var newString, out var newArgError))
            return WriteFailure(newArgError);

        if (oldString.Length == 0)
            return WriteFailure("Error: 'old_string' is empty. Give the exact text to replace, or use write_file to create a file.");
        if (string.Equals(oldString, newString, StringComparison.Ordinal))
            return WriteFailure("Error: 'old_string' and 'new_string' are identical — nothing to change.");

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
            return WriteFailure("Error: Path is outside the assistant files folder.");
        if (SensitivePathGuard.IsBlocked(safePath, out var blockReason))
        {
            _logger.LogWarning("edit_file rejected: sensitive path");
            _logger.SensitiveDebug("edit_file blocked path: {Path}", safePath);
            return WriteFailure($"Error: Refusing to write here — {blockReason}.");
        }

        if (!File.Exists(safePath))
            return WriteFailure($"Error: '{SafeRelative(root, safePath)}' does not exist. Use write_file to create a new file.");

        var (current, readError) = await ReadFileTextAsync(safePath, requested, cancellationToken);
        if (readError is not null) return WriteFailure(readError);

        var occurrences = CountOccurrences(current!, oldString);
        if (occurrences == 0)
            return WriteFailure(
                "Error: 'old_string' was not found in the file. Re-read the file and copy the text exactly, " +
                "without read_file's 'N|' prefixes and with the same whitespace.");

        var replaceAll = GetBoolArg(args, "replace_all");
        if (occurrences > 1 && !replaceAll)
            return WriteFailure(
                $"Error: 'old_string' matches {occurrences} places in the file. Include enough surrounding text " +
                "to make it unique, or pass replace_all=true to change all of them.");

        var updated = current!.Replace(oldString, newString, StringComparison.Ordinal);

        // Everything downstream is keyed off 'content', so the edit rejoins the one write pipeline here.
        var writeArgs = new Dictionary<string, object?>(args) { ["content"] = updated };
        return await PrepareWriteFileAsync(root, writeArgs, cancellationToken, toolName: "edit_file");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private async Task<(object? Result, FilesToolCall? Pending)> PrepareWriteFileAsync(
        string root, IDictionary<string, object?> args, CancellationToken cancellationToken,
        string toolName = "write_file")
    {
        var requested = NormalizePathArg(GetStringArg(args, "path"));

        // ARG HARDENING: distinguish a missing 'content' key from a present-but-empty one.
        // GetStringArg returns "" for a missing key, which would silently write an empty file.
        if (!TryGetRequiredStringArg(args, "content", out var content, out var argError))
            return WriteFailure(argError);

        // INTERNAL-CONTENT GUARD: reject a read_file echo accidentally fed back as content.
        if (LooksLikeReadFileEcho(content))
            return WriteFailure(
                "Error: 'content' looks like read_file output (line-number-prefixed lines, e.g. '12|foo'). " +
                "Pass the raw file contents only, without line-number prefixes or the total_lines header.");

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
            return WriteFailure("Error: Path is outside the assistant files folder.");

        // SENSITIVE-PATH BLOCKLIST (after §0.3 resolution).
        if (SensitivePathGuard.IsBlocked(safePath, out var blockReason))
        {
            _logger.LogWarning("write_file rejected: sensitive path");
            _logger.SensitiveDebug("write_file blocked path: {Path}", safePath);
            return WriteFailure($"Error: Refusing to write here — {blockReason}.");
        }

        // The vault is never provisioned into a run workspace, and the promote walk drops anything under it.
        var vaultAnchor = TaskAmbient.Current?.WorkspaceRoot is null ? null : root;
        if (vaultAnchor is not null && AssistantWorkspace.IsAtOrInsideVaultOf(vaultAnchor, safePath))
        {
            _logger.LogWarning("write_file rejected: the run workspace has no memory vault");
            _logger.SensitiveDebug("write_file vault-target path: {Path}", safePath);
            return WriteFailure(VaultTargetPolicy.WriteRefusal(vaultAnchor, safePath));
        }

        if (content.Length > MaxWriteChars)
            return WriteFailure($"Error: Content is too large ({content.Length} chars, max {MaxWriteChars}).");

        var ext = Path.GetExtension(safePath);
        if (MacroOrTemplateWriteExtensions.Contains(ext))
            return WriteFailure(
                $"Error: '{ext}' files can't be edited by write_file — macro-enabled and template Office " +
                "formats are read-only here (regenerating any part of the package risks losing the macro " +
                "project). Save a .docx/.xlsx copy and edit that instead.");

        if (RenderedReadOnlyExtensions.Contains(ext))
            return WriteFailure(
                $"Error: '{ext}' files are read-only here — read_file returns a rendered view of the " +
                "message (headers, then body), not the file's own bytes, so writing that back would " +
                "destroy the original. Write the text to a new .md or .txt file instead.");

        var exists = File.Exists(safePath);
        var rel = SafeRelative(root, safePath);
        var desc = exists ? $"Update file '{rel}'" : $"Create file '{rel}'";
        bool isDocx = string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase);
        bool isXlsx = string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase);

        // Excel's own sheet-name rules (length/forbidden characters/uniqueness/non-empty) aren't
        // enforced by the OpenXml SDK, so an invalid name would otherwise silently produce a file
        // Excel repair-prompts on open — covers both a brand-new workbook (CreateFresh has no
        // return channel of its own) and a new sheet introduced mid-edit.
        if (isXlsx)
        {
            var sheetNameError = XlsxPatcher.ValidateSheetNames(content);
            if (sheetNameError is not null) return WriteFailure($"Error: {sheetNameError}");
        }

        // TRUE LINE-LEVEL DIFF: compute old→new at prepare time (shown to the user as the approval
        // card's preview, and — for docx — fed to DocxPatcher as the edit script). For a new file
        // the whole content is "added".
        //
        // A plain-text baseline read failure tolerantly falls back to an empty (all-added) baseline
        // — a transient text-read glitch degrading to "treat as new file" is an acceptable fallback.
        // For docx/xlsx it is NOT: silently doing that would turn "patch one paragraph/cell" into a
        // full-document regenerate, exactly the destructive behavior patch-in-place exists to avoid.
        // So a failed/oversized/corrupt structured-document baseline is a hard prepare-time rejection.
        string? oldContent = null;
        DateTime? previewMtime = null;
        if (exists && (isDocx || isXlsx))
        {
            var structured = isDocx
                ? await DroppedFileReader.ReadDocxAsync(safePath, cancellationToken)
                : await DroppedFileReader.ReadXlsxAsync(safePath, cancellationToken);
            if (structured.Status == DroppedFileReader.ReadStatus.TooLarge)
                return WriteFailure("Error: The current document is too large to safely prepare this edit.");
            if (structured.Status == DroppedFileReader.ReadStatus.Failed)
                return WriteFailure($"Error: Could not read the current document to prepare this edit ({structured.Error}).");
            oldContent = structured.Text ?? string.Empty;
            previewMtime = File.GetLastWriteTimeUtc(safePath);
        }
        else if (exists)
        {
            try
            {
                oldContent = File.ReadAllText(safePath);
                previewMtime = File.GetLastWriteTimeUtc(safePath);
            }
            catch { oldContent = null; previewMtime = null; }
        }

        var diff = LineDiff.Compute(oldContent, content);

        if (oldContent is not null && !diff.Any(d => d.Kind is DiffLineKind.Added or DiffLineKind.Removed))
            return WriteFailure(
                "No change: 'content' is byte-identical to the current file, so nothing was written. " +
                "If you meant to change something, the edit did not survive into 'content' — check it and resubmit.");

        // LEAKED-PREFIX GUARD: LooksLikeReadFileEcho cannot see a sparse slip — a leaked line is a bare
        // number with no pipe, so it never counts toward that majority.
        if (oldContent is not null &&
            FindLeakedLineNumbers(diff, content, oldContent, strict: isDocx || isXlsx) is { Count: > 0 } leaked)
        {
            var cited = string.Join("; ", leaked.Take(5).Select(l => $"line {l.LineNumber} is \"{l.Text}\""));
            return WriteFailure(
                $"Error: 'content' has {leaked.Count} line(s) that are a read_file line number rather than file content " +
                $"({cited}{(leaked.Count > 5 ? "; …" : "")}). read_file's 'N|' prefixes are display only — drop them. " +
                "For a small change prefer edit_file, which does not require resubmitting the whole file.");
        }

        // Validate the patch plan (dry run, no mutation) against the file as it stands right now —
        // this is a deterministic prepare-time rejection (ambiguous anchor, formula overwrite,
        // mid-sheet insert, missing sheet, deletion-guard threshold, touched-node cap), so a doomed
        // edit never reaches an approval card.
        if (exists && isDocx)
        {
            try
            {
                using var validateDoc = WordprocessingDocument.Open(safePath, isEditable: false);
                var check = DocxPatcher.Apply(validateDoc, diff, apply: false, DocxPatchLimits);
                if (!check.Success) return WriteFailure($"Error: {check.Error}");
            }
            catch (Exception ex)
            {
                return WriteFailure($"Error: Could not open the current document to prepare this edit ({ex.Message}). It may be open in another application.");
            }
        }
        else if (exists && isXlsx)
        {
            try
            {
                using var validateDoc = SpreadsheetDocument.Open(safePath, isEditable: false);
                var check = XlsxPatcher.Apply(validateDoc, content, apply: false, XlsxPatchLimits);
                if (!check.Success) return WriteFailure($"Error: {check.Error}");
            }
            catch (Exception ex)
            {
                return WriteFailure($"Error: Could not open the current document to prepare this edit ({ex.Message}). It may be open in another application.");
            }
        }

        // STALENESS GUARD: capture the staleness key (session Id) and baseline at PREPARE time;
        // the recorded read may have happened in an earlier turn, but the ambient carries the
        // stable session Id. Don't read TaskAmbient.Current inside the deferred closure (it runs
        // after the approval await, where ambient flow is not guaranteed).
        var taskId = TaskAmbient.Current?.TaskId ?? Guid.Empty;

        // Capture the per-turn file-touch sink at PREPARE time for the same reason as taskId above:
        // ambient flow is not guaranteed inside the deferred execute closure (it runs after the
        // approval await). On a successful write the closure reports the file so an "open" chip appears.
        var touch = TaskAmbient.Current?.OnFileTouched;

        return (null, new FilesToolCall(
            ToolName: toolName,
            Description: desc,
            Details: $"{content.Length} character(s) will be written.",
            TargetPath: rel,
            Execute: () => ExecuteWriteAsync(root, requested, rel, content, oldContent, diff, exists, previewMtime, taskId, vaultAnchor, touch),
            DiffPreview: diff));
    }

    /// <summary>
    /// A prepare-time write rejection delivered as an immediate structured result (no action card).
    /// Mirrors the shape the model already sees for an execute-time failure so the contract is stable.
    /// </summary>
    private static (object? Result, FilesToolCall? Pending) WriteFailure(string message)
        => (WriteResult.Failed(message), null);

    private async Task<object?> ExecuteWriteAsync(
        string root, string requested, string rel, string content, string? oldContent, IReadOnlyList<DiffLine> diff,
        bool existedAtPrepare, DateTime? previewMtime, Guid taskId, string? vaultAnchor,
        Action<FileTouch>? touch = null)
    {
        // Re-validate inside the deferred execution path — the sandbox root might have changed
        // between preparation and confirmation. Re-check the sensitive blocklist for the same reason.
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var finalPath))
            return WriteResult.Failed("Error: Path is outside the assistant files folder.");
        if (SensitivePathGuard.IsBlocked(finalPath, out var blockReason))
            return WriteResult.Failed($"Error: Refusing to write here — {blockReason}.");
        if (vaultAnchor is not null && AssistantWorkspace.IsAtOrInsideVaultOf(vaultAnchor, finalPath))
            return WriteResult.Failed(VaultTargetPolicy.WriteRefusal(vaultAnchor, finalPath));

        try
        {
            var dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existsNow = File.Exists(finalPath);

            // POST-APPROVAL TOCTOU GUARD: the user approved a specific diff computed from the
            // file's state at preview time. If the file changed — or one appeared where the user
            // approved a *create* — between preview and now, the approved diff no longer matches
            // disk, so BLOCK and make the model re-read + re-prepare rather than silently clobber
            // unseen changes. (The benign read→preview gap stays advisory below; the preview the
            // user saw already reflected current disk.)
            if (existsNow && !existedAtPrepare)
            {
                _logger.LogInformation("write_file blocked: a file appeared since the create was previewed");
                return WriteResult.Failed(
                    "Error: A file now exists at this path that was not present when the create was previewed. " +
                    "Re-read the file and submit the write again so it is based on current content.");
            }

            DateTime currentMtime = default;
            if (existsNow)
                currentMtime = File.GetLastWriteTimeUtc(finalPath);

            if (existsNow && previewMtime.HasValue && currentMtime != previewMtime.Value)
            {
                _logger.LogInformation("write_file blocked: file changed on disk since it was previewed");
                return WriteResult.Failed(
                    "Error: The file changed on disk after this edit was previewed, so the approved diff no longer matches. " +
                    "Re-read the file and submit the write again so it is based on current content.");
            }

            // STALENESS (ADVISORY): the model may have read this file in an earlier turn. Warn
            // (don't block) if it changed since that recorded read — the preview above already
            // reflects current disk, so this is only secondary signal for the model.
            string? warning = null;
            if (existsNow && _stalenessStore.CheckStaleness(taskId, finalPath, currentMtime))
                warning = "The file changed on disk after it was last read; this write may overwrite unseen changes.";

            var ext = Path.GetExtension(finalPath);
            if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                var structuredResult = await ExecuteStructuredWriteAsync(ext, finalPath, rel, content, diff, existsNow);
                if (structuredResult is WriteResult { success: true } ok)
                {
                    touch?.Invoke(new FileTouch(finalPath, existsNow ? FileTouchKind.Updated : FileTouchKind.Created));
                    return ClampResult(ok with { _warning = warning ?? ok._warning });
                }
                return structuredResult;
            }

            // DELTA-FILTERED LINT (only NEW errors surface).
            var lint = WriteLintHelper.Lint(finalPath, oldContent, content);

            var write = AtomicTextWriter.Write(finalPath, content);
            int lineCount = CountLines(content);

            _logger.LogInformation(
                "write_file succeeded ({Bytes} bytes, {Lines} lines, crlf={Crlf}, bom={Bom})",
                write.BytesWritten, lineCount, write.UsedCrlf, write.HadBom);
            _logger.SensitiveDebug("write_file path: {Path}", requested);

            // Surface the written file to the active turn's message as an "open file" chip. Use the
            // re-resolved finalPath (the bytes' true location) and existsNow (create vs update at write time).
            touch?.Invoke(new FileTouch(finalPath, existsNow ? FileTouchKind.Updated : FileTouchKind.Created));

            var result = WriteResult.Ok(rel, write.BytesWritten, lineCount, lint, warning, !existsNow);
            return ClampResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "write_file failed");
            return WriteResult.Failed($"Error: Could not write file ({ex.Message}).");
        }
    }

    /// <summary>
    /// Builds a docx/xlsx write into a same-directory temp file (a fresh minimal package when
    /// <paramref name="existedAtExecute"/> is false, otherwise a patch of the original via
    /// <see cref="DocxPatcher"/>/<see cref="XlsxPatcher"/>, re-validated against the SAME diff/content
    /// already approved by the user — deterministic, since the caller's mtime check just proved the
    /// file is byte-identical to what was validated at prepare time), round-trip-validates the result
    /// by re-extracting and comparing against <paramref name="content"/>, and only then commits.
    /// A validation or patch failure leaves the original file untouched and deletes the temp file.
    /// </summary>
    private async Task<object?> ExecuteStructuredWriteAsync(
        string ext, string finalPath, string rel, string content, IReadOnlyList<DiffLine> diff, bool existedAtExecute)
    {
        bool isDocx = string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase);
        var tempPath = AtomicBinaryWriter.CreateTempPath(finalPath);

        try
        {
            if (!existedAtExecute)
            {
                if (isDocx) DocxPatcher.CreateFresh(tempPath, content);
                else XlsxPatcher.CreateFresh(tempPath, content);
            }
            else
            {
                File.Copy(finalPath, tempPath, overwrite: false);
                if (isDocx)
                {
                    using var doc = WordprocessingDocument.Open(tempPath, isEditable: true);
                    var patch = DocxPatcher.Apply(doc, diff, apply: true, DocxPatchLimits);
                    if (!patch.Success)
                    {
                        AtomicBinaryWriter.DiscardTempFile(tempPath);
                        return WriteResult.Failed($"Error: {patch.Error}");
                    }
                    doc.MainDocumentPart!.Document!.Save();
                }
                else
                {
                    using var doc = SpreadsheetDocument.Open(tempPath, isEditable: true);
                    var patch = XlsxPatcher.Apply(doc, content, apply: true, XlsxPatchLimits);
                    if (!patch.Success)
                    {
                        AtomicBinaryWriter.DiscardTempFile(tempPath);
                        return WriteResult.Failed($"Error: {patch.Error}");
                    }
                    doc.WorkbookPart!.Workbook!.Save();
                }
            }

            var roundTripError = await ValidateRoundTripAsync(isDocx, tempPath, content);
            if (roundTripError is not null)
            {
                AtomicBinaryWriter.DiscardTempFile(tempPath);
                _logger.LogError("write_file round-trip validation failed for {Ext}", ext);
                return WriteResult.Failed($"Error: {roundTripError}");
            }

            var bytesWritten = new FileInfo(tempPath).Length;
            AtomicBinaryWriter.CommitTempFile(tempPath, finalPath);

            _logger.LogInformation("write_file succeeded ({Bytes} bytes, structured {Ext})", bytesWritten, ext);
            _logger.SensitiveDebug("write_file path: {Path}", rel);

            return WriteResult.Ok(rel, bytesWritten, CountLines(content), null, null, !existedAtExecute);
        }
        catch (Exception ex)
        {
            AtomicBinaryWriter.DiscardTempFile(tempPath);
            _logger.LogError(ex, "write_file failed (structured {Ext})", ext);
            return WriteResult.Failed($"Error: Could not write file ({ex.Message}).");
        }
    }

    /// <summary>Re-extracts the just-built/patched temp file and compares it against the content the
    /// user approved — a stronger guarantee than "does the package open," since a patch-logic bug
    /// could produce a well-formed but wrong document. Blank lines are excluded from the comparison
    /// on both sides: neither reader ever emits one (a paragraph/row that's genuinely blank is never
    /// visible in the first place), so a model-submitted blank line can't literally round-trip.
    /// Returns null on success, else a message describing the mismatch.</summary>
    private static async Task<string?> ValidateRoundTripAsync(bool isDocx, string tempPath, string submittedContent)
    {
        var reread = isDocx
            ? await DroppedFileReader.ReadDocxAsync(tempPath, CancellationToken.None)
            : await DroppedFileReader.ReadXlsxAsync(tempPath, CancellationToken.None);

        if (reread.Status != DroppedFileReader.ReadStatus.Ok)
            return "internal validation error — the patched document could not be re-read.";

        var expected = NonEmptyLines(submittedContent);
        var actual = NonEmptyLines(reread.Text ?? string.Empty);
        return expected.SequenceEqual(actual, StringComparer.Ordinal)
            ? null
            : "internal validation error — the patched document doesn't match the submitted content.";
    }

    private static string[] NonEmptyLines(string text)
        => text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();

    /// <summary>Counts logical lines (LF-delimited, trailing newline not counted as an extra line).</summary>
    private static int CountLines(string content)
    {
        if (content.Length == 0) return 0;
        int lines = 1;
        foreach (var c in content)
            if (c == '\n') lines++;
        if (content.EndsWith('\n')) lines--; // trailing newline doesn't add a phantom line
        return lines;
    }

    /// <summary>
    /// Honors the 100K-char serialized-result cap. The structural fields are tiny; only lint/_warning
    /// can grow, so truncate those rather than the whole object when the serialized form is over cap.
    /// </summary>
    private static WriteResult ClampResult(WriteResult result)
    {
        const int Cap = 100_000;
        var json = JsonSerializer.Serialize(result);
        if (json.Length <= Cap) return result;

        return result with
        {
            lint = Truncate(result.lint, 2000),
            _warning = Truncate(result._warning, 2000)
        };
    }

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max] + "…(truncated)";

    /// <summary>
    /// INTERNAL-CONTENT GUARD heuristic: returns true when the majority of (non-empty) lines look like
    /// read_file echo — a line number then a pipe (e.g. <c>12|foo</c>) — or the content leads with the
    /// <c>total_lines=</c> header read_file emits. Conservative: requires at least a few lines so a tiny
    /// legitimate file (e.g. a one-line config that happens to start with a digit and a pipe) is not
    /// falsely rejected.
    /// </summary>
    private static bool LooksLikeReadFileEcho(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;

        var lines = content.Split('\n');
        // Strip a trailing-newline empty element.
        int len = lines.Length;
        if (len > 0 && lines[len - 1].Length == 0) len--;
        if (len < 3) return false; // too small to judge confidently

        int nonEmpty = 0, numbered = 0;
        for (int i = 0; i < len; i++)
        {
            var line = lines[i];
            if (line.EndsWith('\r')) line = line[..^1];
            if (line.Length == 0) continue;
            nonEmpty++;
            if (IsLineNumberPrefixed(line)) numbered++;
        }
        if (nonEmpty == 0) return false;

        // A clear read_file echo also carries the header; treat that as a strong signal.
        bool hasHeader = (lines[0].StartsWith("total_lines=", StringComparison.Ordinal));

        // Majority of non-empty lines are N|… → echo.
        return numbered * 2 > nonEmpty || (hasHeader && numbered * 3 >= nonEmpty);
    }

    private static bool IsLineNumberPrefixed(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        return i > 0 && i < line.Length && line[i] == '|';
    }

    /// <summary>
    /// Finds NEW lines that are a read_file line number rather than content. A leak takes more than one
    /// shape — it can be inserted before the line it prefixed, or overwrite that line outright — so the
    /// only reliable common factor is "this line is new and it is a bare line number of this file".
    /// <paramref name="strict"/> accepts exactly that; plain text additionally requires the insert shape,
    /// because a bare number is ordinary content there far more often than in a document.
    /// </summary>
    /// <remarks>
    /// An earlier version exempted a hit whose anchor was itself a bare integer, to keep a numeric-data
    /// file writable. That inverted the guard: once a file had been damaged by a leak, every later leak at
    /// the same place was exempt. The whole-file density test below does that job without the inversion.
    /// </remarks>
    private static List<(int LineNumber, string Text)> FindLeakedLineNumbers(
        IReadOnlyList<DiffLine> diff, string content, string oldContent, bool strict)
    {
        var hits = new List<(int LineNumber, string Text)>();
        var lines = SplitLinesForCompare(content);
        var oldLines = SplitLinesForCompare(oldContent);

        if (IsMostlyBareNumbers(oldLines)) return hits;

        var addedLineNumbers = new HashSet<int>();
        foreach (var d in diff)
            if (d.Kind == DiffLineKind.Added && d.NewLineNumber is { } n)
                addedLineNumbers.Add(n);

        for (int k = 0; k < lines.Length; k++)
        {
            if (!addedLineNumbers.Contains(k + 1)) continue;
            if (!IsBareLineNumber(lines[k], out var value) || value > oldLines.Length) continue;

            if (!strict)
            {
                // The insert shape: the line it was the prefix of follows it verbatim.
                var followed = oldLines[value - 1];
                if (followed.Length == 0 || k + 1 >= lines.Length ||
                    !string.Equals(lines[k + 1], followed, StringComparison.Ordinal))
                    continue;
            }

            hits.Add((k + 1, lines[k]));
        }

        return hits;
    }

    /// <summary>
    /// A file that IS a column of numbers, where a bare-integer line is the content. The bar is
    /// deliberately high (60%): being too lenient here is what a missed leak costs, and real documents
    /// measure near zero — the .docx and .xlsx this was built from are 0.5% and 0%.
    /// </summary>
    private static bool IsMostlyBareNumbers(string[] lines)
    {
        int nonEmpty = 0, numeric = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            nonEmpty++;
            if (IsBareLineNumber(line, out _)) numeric++;
        }
        return nonEmpty > 0 && numeric * 5 >= nonEmpty * 3;
    }

    /// <summary>An unsigned, unpadded decimal — the exact shape <see cref="FormatWindow"/> emits.</summary>
    private static bool IsBareLineNumber(string line, out int value)
    {
        value = 0;
        if (line.Length is 0 or > 9 || line[0] == '0') return false;
        foreach (var c in line)
            if (c is < '0' or > '9') return false;
        return int.TryParse(line, out value);
    }

    /// <summary>Splits the way <see cref="FormatWindow"/> does, so a comparison sees the same lines the model read.</summary>
    private static string[] SplitLinesForCompare(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        return lines;
    }

    /// <summary>
    /// Structured write_file return. <c>FilesToolCall.Execute</c> is <c>Func&lt;Task&lt;object?&gt;&gt;</c>
    /// and the tool loop hands the object straight to <c>FunctionResultContent</c>, which JSON-serializes
    /// it for the provider — so an object return is wire-compatible. snake_case names match the prompt
    /// contract; callers read fields by name and do not rely on null-field omission.
    /// </summary>
    private sealed record WriteResult(
        bool success,
        string? resolved_path,
        long bytes_written,
        int lines,
        string? lint,
        string? _warning,
        string? error,
        bool created)
    {
        public static WriteResult Ok(string rel, long bytes, int lines, string? lint, string? warning, bool created)
            => new(true, rel, bytes, lines, lint, warning, null, created);

        public static WriteResult Failed(string error)
            => new(false, null, 0, 0, null, null, error, false);
    }

    /// <summary>
    /// Prepares a delete. Prepare-time hard failures (path-outside, blocked path, file-not-found)
    /// are deterministic rejections with nothing to approve, so they return an immediate
    /// <c>(Result, null)</c> — no action card. Only a viable delete returns a <c>(null, pending)</c>
    /// confirmation card.
    /// </summary>
    private (object? Result, FilesToolCall? Pending) PrepareDeleteFile(string root, IDictionary<string, object?> args)
    {
        var requested = NormalizePathArg(GetStringArg(args, "path"));

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
            return ("Error: Path is outside the assistant files folder.", null);

        // SENSITIVE-PATH BLOCKLIST (after §0.3 resolution) — symmetric with write_file.
        // delete is irreversible, so the same protected roots that can't be written can't be deleted.
        if (SensitivePathGuard.IsBlocked(safePath, out var prepBlockReason))
        {
            _logger.LogWarning("delete_file rejected: sensitive path");
            _logger.SensitiveDebug("delete_file blocked path: {Path}", safePath);
            return ($"Error: Refusing to delete here — {prepBlockReason}.", null);
        }

        if (!File.Exists(safePath))
            return ($"Error: File '{requested}' not found.", null);

        var rel = SafeRelative(root, safePath);

        return (null, new FilesToolCall(
            ToolName: "delete_file",
            Description: $"Delete file '{rel}'",
            Details: "This permanently removes the file.",
            TargetPath: rel,
            Execute: () =>
            {
                // Re-validate inside the deferred execution path (the sandbox root might have changed
                // between preparation and confirmation) and re-check the blocklist for the same reason.
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var finalPath))
                    return Task.FromResult<object?>("Error: Path is outside the assistant files folder.");
                if (SensitivePathGuard.IsBlocked(finalPath, out var blockReason))
                    return Task.FromResult<object?>($"Error: Refusing to delete here — {blockReason}.");

                try
                {
                    File.Delete(finalPath);
                    _logger.LogInformation("delete_file succeeded");
                    _logger.SensitiveDebug("delete_file path: {Path}", requested);
                    return Task.FromResult<object?>($"File '{rel}' deleted.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "delete_file failed");
                    return Task.FromResult<object?>($"Error: Could not delete file ({ex.Message}).");
                }
            }));
    }

    private static string SafeRelative(string root, string fullPath)
    {
        var rootWithSep = SafeFolderPath.WithTrailingSeparator(root);
        return fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(rootWithSep.Length)
            : fullPath;
    }

    // Schema methods — signatures only, used by AIFunctionFactory for tool metadata
    [Description("List text files in the assistant files folder. Ignored paths (built-in defaults such as .git/bin/obj/node_modules, plus any .gitignore/.piaignore entries in the folder) are omitted.")]
    private static string ListFilesSchema(
        [Description("Optional file-name glob, e.g. '*.md' or 'notes*' (matched against file names at any depth). Must not contain a path separator. Defaults to all files.")] string? pattern = null) => "";

    [Description("Read the contents of a text file (also .docx/.xlsx/.msg/.eml, extracted as text) in the assistant files folder. Output is line-numbered as LINE|CONTENT (1-indexed), windowed by offset/limit, and prefixed with total_lines.")]
    private static string ReadFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path,
        [Description("1-indexed line to start reading from (default 1, minimum 1).")] int offset = 1,
        [Description("Maximum number of lines to return (default 500, maximum 2000).")] int limit = 500) => "";

    [Description("Create or overwrite a text file (also .docx/.xlsx — only the changed paragraphs/cells are touched) in the assistant files folder")]
    private static string WriteFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path,
        [Description("Full new contents of the file. Raw content only: no 'N|' line-number prefixes, no 'total_lines=' header.")] string content) => "";

    [Description("Replace an exact piece of text in an existing file (also .docx/.xlsx). Preferred over write_file for editing an existing file — it does not require resubmitting the whole file.")]
    private static string EditFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path,
        [Description("Exact text to replace, as read_file shows it without the 'N|' prefix. Must be unique in the file unless replace_all is true. Use \\n for a line break.")] string old_string,
        [Description("Text to put in its place. Empty string deletes the matched text.")] string new_string,
        [Description("Replace every occurrence instead of requiring exactly one (default false).")] bool replace_all = false) => "";

    [Description("Delete a file from the assistant files folder")]
    private static string DeleteFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path) => "";

    [Description("Find files by name or path glob in the assistant files folder. Read-only. Ignored paths (built-in defaults such as .git/bin/obj/node_modules, plus any .gitignore/.piaignore in the folder) are skipped.")]
    private static string FindFilesSchema(
        [Description("Glob pattern matched against the file path relative to the searched folder — e.g. '*.md' or 'docs/**/*.md'.")] string pattern,
        [Description("Optional subdirectory (relative to the assistant files folder) to scope the search. Defaults to the whole folder.")] string? path = null,
        [Description("Maximum number of results to return (default 100, maximum 500). Results are sorted alphabetically, so a small limit returns the first names in that order, not a representative sample.")] int limit = 100) => "";

    [Description("Search text files in the assistant files folder for a regular-expression pattern. Read-only. Ignored paths (built-in defaults such as .git/bin/obj/node_modules, plus any .gitignore/.piaignore in the folder) are skipped.")]
    private static string SearchFilesSchema(
        [Description("Regular expression to search for, applied per line. Capitalisation is ignored — write it however reads naturally.")] string pattern,
        [Description("Optional subdirectory (relative to the assistant files folder) to scope the search. Defaults to the whole folder.")] string? path = null,
        [Description("Optional file glob restricting which files are searched, matched against the path relative to the searched folder — e.g. '*.cs' or 'docs/**/*.md'.")] string? include = null,
        [Description("Output mode: 'content' (matching lines, default), 'files' (matching file paths only), or 'count' (number of matches per file).")] string? mode = null,
        [Description("1-indexed result to start from (default 1, minimum 1).")] int offset = 1,
        [Description("Maximum number of results to return (default 100, maximum 500).")] int limit = 100,
        [Description("Set true to match capitalisation exactly, e.g. to find the marker TODO without also matching the word 'todo'. Default false.")] bool case_sensitive = false) => "";

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value))
        {
            if (value is JsonElement element)
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
            return value?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// Missing-vs-present accessor for required string args. Returns false (with a corrective error)
    /// when the key is absent or null (so a dropped 'content' never silently writes an empty file) or
    /// when the value is present but not a JSON string (so an object/array isn't coerced to a file).
    /// An explicit empty string is valid (intentional truncation to empty).
    /// </summary>
    private static bool TryGetRequiredStringArg(
        IDictionary<string, object?> args, string key, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!args.TryGetValue(key, out var raw) || raw is null)
        {
            error = $"Error: '{key}' is missing. Re-emit the call with the full '{key}' as a JSON string.";
            return false;
        }

        if (raw is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                error = $"Error: '{key}' is null. Re-emit the call with the full '{key}' as a JSON string.";
                return false;
            }
            if (element.ValueKind != JsonValueKind.String)
            {
                error = $"Error: '{key}' must be a JSON string, not {element.ValueKind.ToString().ToLowerInvariant()}.";
                return false;
            }
            value = element.GetString() ?? string.Empty;
            return true;
        }

        if (raw is string s)
        {
            value = s;
            return true;
        }

        error = $"Error: '{key}' must be a JSON string.";
        return false;
    }

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value) && value is not null)
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null) return null;
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : element.GetRawText();
            }
            var str = value.ToString();
            return string.IsNullOrEmpty(str) ? null : str;
        }
        return null;
    }

    private static int GetOptionalIntArg(IDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n))
                return n;
            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), out var parsed))
                return parsed;
            return defaultValue;
        }

        if (value is int i) return i;
        if (value is long l) return (int)l;
        return int.TryParse(value.ToString(), out var fallback) ? fallback : defaultValue;
    }

    /// <summary>Tolerates a real bool, a JSON bool and the string forms models sometimes emit.</summary>
    private static bool GetBoolArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return false;

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => bool.TryParse(element.GetString(), out var s) && s,
                _ => false,
            };
        }

        if (value is bool b) return b;
        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }
}
