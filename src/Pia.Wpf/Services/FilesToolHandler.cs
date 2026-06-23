using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private const int MaxReadBytes = 256 * 1024;          // 256 KB read cap
    private const int MaxWriteChars = 512 * 1024;         // 512 K chars write cap
    private const int MaxListEntries = 500;

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
                "Delete a file inside the assistant files folder.")
        ];
    }

    public Task<(object? Result, FilesToolCall? PendingAction)> HandleToolCallAsync(
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
            return Task.FromResult<(object?, FilesToolCall?)>((
                "Error: No assistant files folder is configured. Ask the user to set one under Settings → Assistant.",
                null));
        }

        (object? result, FilesToolCall? pending) = toolCall.Name switch
        {
            "list_files" => (HandleListFiles(root, args), null),
            "read_file"  => (HandleReadFile(root, args), null),
            "write_file" => ((object?)null, PrepareWriteFile(root, args)),
            "delete_file" => ((object?)null, PrepareDeleteFile(root, args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (FilesToolCall?)null)
        };
        return Task.FromResult((result, pending));
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

    private object HandleReadFile(string root, IDictionary<string, object?> args)
    {
        var requested = GetStringArg(args, "path");
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requested, out var safePath))
        {
            _logger.LogWarning("read_file rejected path outside sandbox");
            _logger.SensitiveDebug("read_file rejected path: {Path}", requested);
            return "Error: Path is outside the assistant files folder.";
        }

        if (!File.Exists(safePath)) return $"Error: File '{requested}' not found.";

        try
        {
            var info = new FileInfo(safePath);
            if (info.Length > MaxReadBytes)
                return $"Error: File is too large to read ({info.Length} bytes, max {MaxReadBytes}).";

            var content = File.ReadAllText(safePath);
            _logger.LogInformation("read_file succeeded ({Bytes} bytes)", info.Length);
            _logger.SensitiveDebug("read_file path: {Path}", requested);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file");
            return $"Error: Could not read file ({ex.Message}).";
        }
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

    [Description("Read the contents of a text file in the assistant files folder")]
    private static string ReadFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path) => "";

    [Description("Create or overwrite a text file in the assistant files folder")]
    private static string WriteFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path,
        [Description("Full new contents of the file.")] string content) => "";

    [Description("Delete a file from the assistant files folder")]
    private static string DeleteFileSchema(
        [Description("Relative path to the file. Must stay inside the assistant files folder.")] string path) => "";

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
}
