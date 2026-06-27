using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // read_file windowing / cap regime.
    // Input ceiling: raw file bytes loaded into memory. Reconciled with DroppedFileReader
    // (1 MB extracted text for structured docs, 8 MB raw container) — we adopt the same
    // 1 MB ceiling for plain-text reads so the two read paths share one effective limit.
    private const long MaxReadFileBytes = DroppedFileReader.MaxTextBytes; // 1 MB raw text ceiling
    private const int DefaultReadLimit = 500;             // default window line count
    private const int MaxReadLimit = 2000;                // hard cap on window line count
    private const int MaxFormattedWindowChars = 100 * 1024; // ~100K-char cap on formatted output

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
    /// True when the file tools are enabled AND a usable sandbox folder is configured. Used by the
    /// plugin host to suppress tool registration and the system prompt while disabled. The folder is
    /// always set now (the vault lives under it), so <see cref="AppSettings.AssistantFileToolsEnabled"/>
    /// is the explicit on/off switch.
    /// </summary>
    public bool IsAvailable => _toolsEnabled && _currentFolder is not null;

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
                "List text files inside the user's assistant files folder. Returns relative paths the other file tools accept."),

            AIFunctionFactory.Create(ReadFileSchema, "read_file",
                "Read the contents of a text file inside the assistant files folder. Use this before summarizing or updating a file."),

            AIFunctionFactory.Create(WriteFileSchema, "write_file",
                "Create or overwrite a text file inside the assistant files folder. Used for both creating new files and updating existing ones."),

            AIFunctionFactory.Create(DeleteFileSchema, "delete_file",
                "Delete a file inside the assistant files folder."),

            AIFunctionFactory.Create(SearchFilesSchema, "search_files",
                "Search text files inside the assistant files folder for a regular-expression pattern. Read-only; returns matching lines, matching file paths, or a count.")
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

        var baseRoot = _currentFolder;
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
            "read_file"  => (await HandleReadFileAsync(root, args, cancellationToken), null),
            "write_file" => PrepareWriteFile(root, args),
            "delete_file" => PrepareDeleteFile(root, args),
            "search_files" => (HandleSearchFiles(root, args), null),
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

        List<string> rels;
        try
        {
            rels = CollectRelativeFiles(
                root,
                string.IsNullOrWhiteSpace(pattern) ? "*" : pattern!,
                MaxListEntries);
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
    /// Shared sandbox file enumeration for <c>list_files</c> and the <c>@Files</c> picker.
    /// Walks <paramref name="root"/> recursively and applies the identical filtering both
    /// consumers must agree on: canonical-containment (discard anything that resolves outside
    /// root via junction/symlink) and the sensitive-path blocklist. Returns sandbox-relative
    /// paths (native separators, derived from the enumerated path) capped at <paramref name="max"/>.
    /// Throws on enumeration failure (e.g. an invalid glob pattern) — callers translate that into
    /// their own error surface.
    /// </summary>
    private static List<string> CollectRelativeFiles(string root, string searchPattern, int max)
    {
        var entries = Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories);

        var rels = new List<string>();
        foreach (var full in entries)
        {
            // Canonicalizing safety net: discard anything that, after junction/symlink
            // resolution, isn't inside root. (Supersedes the old lexical StartsWith net.)
            if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out var canon)) continue;
            // Don't list protected system/app-data files even inside a broad sandbox (symmetric
            // with read/search/write/delete — the blocklist applies regardless of sandbox scope).
            if (SensitivePathGuard.IsBlocked(canon, out _)) continue;
            // Display path is derived from the (already lexically-under-root) enumerated path,
            // not the junction-resolved one.
            rels.Add(SafeRelative(root, full));
            if (rels.Count >= max) break;
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
            all = CollectRelativeFiles(root, "*", MaxListEntries);
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

    private static readonly HashSet<string> SearchIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules"
    };

    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Hand-rolled, in-process, synchronous regex search over the sandbox. Walks the tree with a
    /// manual stack (pruning the minimal ignore set before descending so we never spend the scan
    /// budget on .git/bin/obj/node_modules), matches each line against the user regex (guarded by a
    /// match timeout against catastrophic backtracking), and emits a diagnostics block followed by a
    /// results body. Read-only: the caller wraps the return as (result, null) — no action card.
    /// </summary>
    private object HandleSearchFiles(string root, IDictionary<string, object?> args)
    {
        var pattern = GetStringArg(args, "pattern");
        if (string.IsNullOrEmpty(pattern))
            return "Error: A 'pattern' (regular expression) is required.";

        var mode = NormalizeSearchMode(GetOptionalStringArg(args, "mode"));
        var offset = Math.Max(1, GetOptionalIntArg(args, "offset", 1));
        var limit = Math.Clamp(GetOptionalIntArg(args, "limit", 100), 1, MaxMatches);

        // Resolve the search root. A missing/empty/"." path means the whole sandbox; the
        // permissive resolver rejects the root itself, so special-case it rather than route it.
        var requestedPath = GetOptionalStringArg(args, "path");
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
            var suggestions = SuggestSimilarDirectories(root, requestedPath!);
            var msg = $"Error: Path '{requestedPath}' was not found inside the assistant files folder.";
            return suggestions.Count > 0
                ? msg + " Did you mean: " + string.Join(", ", suggestions) + "?"
                : msg;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, SearchRegexTimeout);
        }
        catch (ArgumentException ex)
        {
            // Invalid regex is a diagnostic, not a crash.
            _logger.LogInformation("search_files rejected invalid regex");
            _logger.SensitiveDebug("search_files invalid pattern {Pattern}: {Error}", pattern, ex.Message);
            return $"Error: Invalid regular expression: {ex.Message}";
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
        bool scanTruncated = false;
        bool matchTruncated = false;
        bool regexTimedOut = false;

        var stack = new Stack<string>();
        stack.Push(searchRoot);

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
                    var name = Path.GetFileName(sub);
                    if (SearchIgnoredDirs.Contains(name)) continue; // prune by segment, not substring
                    stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch { files = []; }

                foreach (var full in files)
                {
                    if (filesScanned >= MaxFilesScanned) { scanTruncated = true; break; }
                    filesScanned++;

                    // Defense in depth: re-filter every path through the canonicalized root prefix,
                    // discarding anything that (after junction/symlink resolution) escapes the base.
                    if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out var canon)) continue;
                    if (!canon.StartsWith(canonRootWithSep, StringComparison.OrdinalIgnoreCase)) continue;
                    // Don't surface contents of protected system/app-data files even inside a broad sandbox.
                    if (SensitivePathGuard.IsBlocked(canon, out _)) continue;

                    string[] lines;
                    try
                    {
                        // Per-file size guard mirroring read_file (:536): skip oversized files instead of
                        // loading them whole into memory (a multi-GB file in a cloned repo would otherwise
                        // allocate the entire file and could OOM the process).
                        if (new FileInfo(full).Length > MaxReadFileBytes) continue;
                        var bytes = File.ReadAllBytes(full);
                        if (LooksBinary(bytes)) continue; // skip binaries
                        lines = DecodeText(bytes).Split('\n');
                    }
                    catch { continue; }

                    // Emit a SANDBOX-ROOT-relative path (the same form list_files/read_file accept) so a
                    // scoped search hit is round-trippable: read_file(<emitted path>) resolves to the hit.
                    var rel = SafeRelative(root, full);

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_files walk failed");
            return $"Error: Search failed ({ex.Message}).";
        }

        if (regexTimedOut)
            diagnostics.Add($"Warning: the pattern took too long to evaluate (over {SearchRegexTimeout.TotalSeconds:0}s) and was stopped; results may be incomplete. Simplify the regular expression.");
        if (scanTruncated)
            diagnostics.Add($"Warning: stopped after scanning {MaxFilesScanned} files; results may be incomplete. Narrow the search with a 'path'.");
        if (matchTruncated)
            diagnostics.Add($"Note: more than {MaxMatches} matches; collection stopped at {MaxMatches} (truncated at {MaxMatches}).");

        _logger.LogInformation(
            "search_files scanned {Files} file(s), {Matches} match(es), mode {Mode}",
            filesScanned, matches.Count, mode);
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
    /// up to three sandbox subdirectory names that share the leaf name's prefix or substring.
    /// </summary>
    private static List<string> SuggestSimilarDirectories(string root, string requestedPath)
    {
        var leaf = Path.GetFileName(requestedPath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf)) return [];

        var rootWithSep = SafeFolderPath.WithTrailingSeparator(root);
        var hits = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(dir);
                if (SearchIgnoredDirs.Contains(name)) continue;
                if (name.Contains(leaf, StringComparison.OrdinalIgnoreCase) ||
                    leaf.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = dir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                        ? dir.Substring(rootWithSep.Length) : name;
                    hits.Add(rel);
                    if (hits.Count >= 3) break;
                }
            }
        }
        catch { /* best effort */ }
        return hits;
    }

    private async Task<object> HandleReadFileAsync(
        string root, IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var requested = GetStringArg(args, "path");
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

        if (!File.Exists(safePath)) return $"Error: File '{requested}' not found.";

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
    /// then windows + records staleness) and <see cref="ReadPromptPreviewAsync"/> (which caps for the
    /// prompt). <paramref name="requested"/> is the model-supplied path, used only in error text.
    /// </summary>
    private async Task<(string? Text, string? Error)> ReadFileTextAsync(
        string safePath, string requested, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(safePath);

        if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            var docx = await DroppedFileReader.ReadDocxAsync(safePath, cancellationToken);
            if (docx.Status == DroppedFileReader.ReadStatus.TooLarge)
                return (null, $"Error: File is too large to read (max {MaxReadFileBytes / 1024} KB of extracted text).");
            if (docx.Status == DroppedFileReader.ReadStatus.Failed)
                return (null, $"Error: Could not read file ({docx.Error}).");
            return (docx.Text ?? string.Empty, null);
        }

        if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            var xlsx = await DroppedFileReader.ReadXlsxAsync(safePath, cancellationToken);
            if (xlsx.Status == DroppedFileReader.ReadStatus.TooLarge)
                return (null, $"Error: File is too large to read (max {MaxReadFileBytes / 1024} KB of extracted text).");
            if (xlsx.Status == DroppedFileReader.ReadStatus.Failed)
                return (null, $"Error: Could not read file ({xlsx.Error}).");
            return (xlsx.Text ?? string.Empty, null);
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
        return new FilePromptPreview(relativePath, Found: true, preview, total, shown, truncated, Error: null);
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
    private (object? Result, FilesToolCall? Pending) PrepareWriteFile(string root, IDictionary<string, object?> args)
    {
        var requested = GetStringArg(args, "path");

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

        if (content.Length > MaxWriteChars)
            return WriteFailure($"Error: Content is too large ({content.Length} chars, max {MaxWriteChars}).");

        var exists = File.Exists(safePath);
        var rel = SafeRelative(root, safePath);
        var desc = exists ? $"Update file '{rel}'" : $"Create file '{rel}'";

        // TRUE LINE-LEVEL DIFF: compute old→new at prepare time. For a new file the whole
        // content is "added". Read failures fall back to an empty (all-added) baseline.
        // Capture the baseline's mtime alongside its content so the post-approval guard in
        // ExecuteWriteAsync can detect an out-of-band change to the exact bytes the user previewed.
        string? oldContent = null;
        DateTime? previewMtime = null;
        if (exists)
        {
            try
            {
                oldContent = File.ReadAllText(safePath);
                previewMtime = File.GetLastWriteTimeUtc(safePath);
            }
            catch { oldContent = null; previewMtime = null; }
        }
        var diff = LineDiff.Compute(oldContent, content);

        // STALENESS GUARD: capture the staleness key (session Id) and baseline at PREPARE time;
        // the recorded read may have happened in an earlier turn, but the ambient carries the
        // stable session Id. Don't read TaskAmbient.Current inside the deferred closure (it runs
        // after the approval await, where ambient flow is not guaranteed).
        var taskId = TaskAmbient.Current?.TaskId ?? Guid.Empty;

        return (null, new FilesToolCall(
            ToolName: "write_file",
            Description: desc,
            Details: $"{content.Length} character(s) will be written.",
            TargetPath: rel,
            Execute: () => ExecuteWriteAsync(root, requested, rel, content, oldContent, exists, previewMtime, taskId),
            DiffPreview: diff));
    }

    /// <summary>
    /// A prepare-time write rejection delivered as an immediate structured result (no action card).
    /// Mirrors the shape the model already sees for an execute-time failure so the contract is stable.
    /// </summary>
    private static (object? Result, FilesToolCall? Pending) WriteFailure(string message)
        => (WriteResult.Failed(message), null);

    private Task<object?> ExecuteWriteAsync(
        string root, string requested, string rel, string content, string? oldContent,
        bool existedAtPrepare, DateTime? previewMtime, Guid taskId)
    {
        // Re-validate inside the deferred execution path — the sandbox root might have changed
        // between preparation and confirmation. Re-check the sensitive blocklist for the same reason.
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var finalPath))
            return Task.FromResult<object?>(WriteResult.Failed("Error: Path is outside the assistant files folder."));
        if (SensitivePathGuard.IsBlocked(finalPath, out var blockReason))
            return Task.FromResult<object?>(WriteResult.Failed($"Error: Refusing to write here — {blockReason}."));

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
                return Task.FromResult<object?>(WriteResult.Failed(
                    "Error: A file now exists at this path that was not present when the create was previewed. " +
                    "Re-read the file and submit the write again so it is based on current content."));
            }

            DateTime currentMtime = default;
            if (existsNow)
                currentMtime = File.GetLastWriteTimeUtc(finalPath);

            if (existsNow && previewMtime.HasValue && currentMtime != previewMtime.Value)
            {
                _logger.LogInformation("write_file blocked: file changed on disk since it was previewed");
                return Task.FromResult<object?>(WriteResult.Failed(
                    "Error: The file changed on disk after this edit was previewed, so the approved diff no longer matches. " +
                    "Re-read the file and submit the write again so it is based on current content."));
            }

            // STALENESS (ADVISORY): the model may have read this file in an earlier turn. Warn
            // (don't block) if it changed since that recorded read — the preview above already
            // reflects current disk, so this is only secondary signal for the model.
            string? warning = null;
            if (existsNow && _stalenessStore.CheckStaleness(taskId, finalPath, currentMtime))
                warning = "The file changed on disk after it was last read; this write may overwrite unseen changes.";

            // DELTA-FILTERED LINT (only NEW errors surface).
            var lint = WriteLintHelper.Lint(finalPath, oldContent, content);

            var write = AtomicTextWriter.Write(finalPath, content);
            int lineCount = CountLines(content);

            _logger.LogInformation(
                "write_file succeeded ({Bytes} bytes, {Lines} lines, crlf={Crlf}, bom={Bom})",
                write.BytesWritten, lineCount, write.UsedCrlf, write.HadBom);
            _logger.SensitiveDebug("write_file path: {Path}", requested);

            var result = WriteResult.Ok(rel, write.BytesWritten, lineCount, lint, warning, !existsNow);
            return Task.FromResult<object?>(ClampResult(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "write_file failed");
            return Task.FromResult<object?>(WriteResult.Failed($"Error: Could not write file ({ex.Message})."));
        }
    }

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
        var requested = GetStringArg(args, "path");

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
    [Description("List text files in the assistant files folder")]
    private static string ListFilesSchema(
        [Description("Optional glob pattern, e.g. '*.md'. Defaults to all files.")] string? pattern = null) => "";

    [Description("Read the contents of a text file in the assistant files folder. Output is line-numbered as LINE|CONTENT (1-indexed), windowed by offset/limit, and prefixed with total_lines.")]
    private static string ReadFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path,
        [Description("1-indexed line to start reading from (default 1, minimum 1).")] int offset = 1,
        [Description("Maximum number of lines to return (default 500, maximum 2000).")] int limit = 500) => "";

    [Description("Create or overwrite a text file in the assistant files folder")]
    private static string WriteFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path,
        [Description("Full new contents of the file.")] string content) => "";

    [Description("Delete a file from the assistant files folder")]
    private static string DeleteFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path) => "";

    [Description("Search text files in the assistant files folder for a regular-expression pattern. Read-only. .git/bin/obj/node_modules are skipped.")]
    private static string SearchFilesSchema(
        [Description("Regular expression to search for, applied per line.")] string pattern,
        [Description("Optional subdirectory (relative to the assistant files folder) to scope the search. Defaults to the whole folder.")] string? path = null,
        [Description("Output mode: 'content' (matching lines, default), 'files' (matching file paths only), or 'count' (number of matches per file).")] string? mode = null,
        [Description("1-indexed result to start from (default 1, minimum 1).")] int offset = 1,
        [Description("Maximum number of results to return (default 100, maximum 500).")] int limit = 100) => "";

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
}
