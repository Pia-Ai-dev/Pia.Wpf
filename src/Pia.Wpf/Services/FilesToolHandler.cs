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
            UpdateFolder(settings.AssistantFilesFolder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load initial AssistantFilesFolder");
        }

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// True when a usable sandbox folder is configured. Used by the plugin host
    /// to suppress tool registration and the system prompt while disabled.
    /// </summary>
    public bool IsAvailable => _currentFolder is not null;

    private void OnSettingsChanged(object? sender, AppSettings settings)
        => UpdateFolder(settings.AssistantFilesFolder);

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

        var root = _currentFolder;
        if (root is null || !Directory.Exists(root))
        {
            return (
                "Error: No assistant files folder is configured. Ask the user to set one under Settings → Assistant.",
                null);
        }

        return toolCall.Name switch
        {
            "list_files" => (HandleListFiles(root, args), null),
            "read_file"  => (await HandleReadFileAsync(root, args, cancellationToken), null),
            "write_file" => ((object?)null, PrepareWriteFile(root, args)),
            "delete_file" => ((object?)null, PrepareDeleteFile(root, args)),
            "search_files" => (HandleSearchFiles(root, args), null),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (FilesToolCall?)null)
        };
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

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(
                root,
                string.IsNullOrWhiteSpace(pattern) ? "*" : pattern!,
                SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate files in sandbox");
            return $"Error: Could not list files ({ex.Message}).";
        }

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root : root + Path.DirectorySeparatorChar;

        var rels = new List<string>();
        foreach (var full in entries)
        {
            // Canonicalizing safety net: discard anything that, after junction/symlink
            // resolution, isn't inside root. (Supersedes the old lexical StartsWith net.)
            if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, full, out _)) continue;
            // Display path is derived from the (already lexically-under-root) enumerated path,
            // not the junction-resolved one.
            rels.Add(full.Substring(rootWithSep.Length));
            if (rels.Count >= MaxListEntries) break;
        }

        if (rels.Count == 0) return "No files found in the assistant files folder.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {rels.Count} file(s) (relative paths):");
        foreach (var r in rels) sb.AppendLine($"  {r}");
        if (rels.Count == MaxListEntries) sb.AppendLine($"  ... (truncated at {MaxListEntries})");
        return sb.ToString();
    }

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

        var rootWithSep = searchRoot.EndsWith(Path.DirectorySeparatorChar)
            ? searchRoot : searchRoot + Path.DirectorySeparatorChar;
        var canonRootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root : root + Path.DirectorySeparatorChar;

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

                    string[] lines;
                    try
                    {
                        var bytes = File.ReadAllBytes(full);
                        if (LooksBinary(bytes)) continue; // skip binaries
                        lines = DecodeText(bytes).Split('\n');
                    }
                    catch { continue; }

                    var rel = full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(rootWithSep.Length)
                        : full;

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

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
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

        if (!File.Exists(safePath)) return $"Error: File '{requested}' not found.";

        // offset is 1-indexed (default 1, min 1); limit defaults to 500, clamped to [1, 2000].
        var offset = Math.Max(1, GetOptionalIntArg(args, "offset", 1));
        var limit = Math.Clamp(GetOptionalIntArg(args, "limit", DefaultReadLimit), 1, MaxReadLimit);
        var windowRequested = args.ContainsKey("offset") || args.ContainsKey("limit");

        try
        {
            // Route by extension FIRST (structured docs are zip containers full of NUL bytes —
            // sniffing them as binary would falsely reject them); NUL-sniff only on the text path.
            var ext = Path.GetExtension(safePath);
            string text;

            if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase))
            {
                var docx = await DroppedFileReader.ReadDocxAsync(safePath, cancellationToken);
                if (docx.Status == DroppedFileReader.ReadStatus.TooLarge)
                    return $"Error: File is too large to read (max {MaxReadFileBytes / 1024} KB of extracted text).";
                if (docx.Status == DroppedFileReader.ReadStatus.Failed)
                    return $"Error: Could not read file ({docx.Error}).";
                text = docx.Text ?? string.Empty;
            }
            else if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ext, ".xlsm", StringComparison.OrdinalIgnoreCase))
            {
                var xlsx = await DroppedFileReader.ReadXlsxAsync(safePath, cancellationToken);
                if (xlsx.Status == DroppedFileReader.ReadStatus.TooLarge)
                    return $"Error: File is too large to read (max {MaxReadFileBytes / 1024} KB of extracted text).";
                if (xlsx.Status == DroppedFileReader.ReadStatus.Failed)
                    return $"Error: Could not read file ({xlsx.Error}).";
                text = xlsx.Text ?? string.Empty;
            }
            else if (IsImageExtension(ext))
            {
                return $"Error: '{requested}' is an unsupported binary file (image); attach the image instead.";
            }
            else
            {
                var info = new FileInfo(safePath);
                if (info.Length > MaxReadFileBytes)
                    return $"Error: File is too large to read ({info.Length} bytes, max {MaxReadFileBytes} bytes). " +
                           "Narrow the read with offset/limit is not possible for files over the raw-byte ceiling.";

                var bytes = await File.ReadAllBytesAsync(safePath, cancellationToken);
                if (LooksBinary(bytes))
                    return $"Error: '{requested}' appears to be a binary file (contains NUL bytes) and cannot be read as text.";

                // Decode honoring a leading BOM (UTF-8/UTF-16); default to UTF-8.
                text = DecodeText(bytes);
            }

            var result = FormatWindow(text, offset, limit, windowRequested);

            // Record the observed mtime keyed by the canonicalized resolved path (not the model string).
            _stalenessStore.RecordRead(TaskAmbient.Current ?? Guid.Empty, safePath, File.GetLastWriteTimeUtc(safePath));

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

    private FilesToolCall PrepareWriteFile(string root, IDictionary<string, object?> args)
    {
        var requested = GetStringArg(args, "path");
        var content = GetStringArg(args, "content");

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
            return new FilesToolCall("write_file", "Invalid path", null, null,
                () => Task.FromResult<object?>("Error: Path is outside the assistant files folder."));

        if (content.Length > MaxWriteChars)
            return new FilesToolCall("write_file", "Content too large", null, null,
                () => Task.FromResult<object?>($"Error: Content is too large ({content.Length} chars, max {MaxWriteChars})."));

        var exists = File.Exists(safePath);
        var rel = SafeRelative(root, safePath);
        var desc = exists
            ? $"Update file '{rel}'"
            : $"Create file '{rel}'";

        return new FilesToolCall(
            ToolName: "write_file",
            Description: desc,
            Details: $"{content.Length} character(s) will be written.",
            TargetPath: rel,
            Execute: () =>
            {
                // Re-validate inside the deferred execution path — the sandbox
                // root might have changed between preparation and confirmation.
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var finalPath))
                    return Task.FromResult<object?>("Error: Path is outside the assistant files folder.");

                try
                {
                    var dir = Path.GetDirectoryName(finalPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(finalPath, content);
                    _logger.LogInformation("write_file succeeded ({Bytes} chars)", content.Length);
                    _logger.SensitiveDebug("write_file path: {Path}", requested);
                    return Task.FromResult<object?>(exists
                        ? $"File '{rel}' updated."
                        : $"File '{rel}' created.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "write_file failed");
                    return Task.FromResult<object?>($"Error: Could not write file ({ex.Message}).");
                }
            });
    }

    private FilesToolCall PrepareDeleteFile(string root, IDictionary<string, object?> args)
    {
        var requested = GetStringArg(args, "path");

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
            return new FilesToolCall("delete_file", "Invalid path", null, null,
                () => Task.FromResult<object?>("Error: Path is outside the assistant files folder."));

        if (!File.Exists(safePath))
            return new FilesToolCall("delete_file", "File not found", null, null,
                () => Task.FromResult<object?>($"Error: File '{requested}' not found."));

        var rel = SafeRelative(root, safePath);

        return new FilesToolCall(
            ToolName: "delete_file",
            Description: $"Delete file '{rel}'",
            Details: "This permanently removes the file.",
            TargetPath: rel,
            Execute: () =>
            {
                if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var finalPath))
                    return Task.FromResult<object?>("Error: Path is outside the assistant files folder.");

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
            });
    }

    private static string SafeRelative(string root, string fullPath)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root : root + Path.DirectorySeparatorChar;
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
