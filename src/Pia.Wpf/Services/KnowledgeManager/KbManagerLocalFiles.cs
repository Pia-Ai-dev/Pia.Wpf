using System.IO;
using System.Text;
using Pia.Infrastructure;
using Pia.Shared.Knowledge;

namespace Pia.Services.KnowledgeManager;

public sealed record KbLocalFile(string FullPath, string RelativePath, string Content, string ContentType, long SizeBytes);

/// <summary>The sandbox check stops a prompt-injected model from lifting an outside file into a knowledge base other people search.</summary>
public static class KbManagerLocalFiles
{
    private const int MaxFileNameChars = 120;
    private const int MaxNumberedCopies = 999;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string? ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" => KbManagerLimits.PlainText,
        ".md" or ".markdown" => KbManagerLimits.Markdown,
        _ => null,
    };

    public static bool TryRead(string root, string? requestedPath, out KbLocalFile file, out string error)
    {
        file = null!;
        if (!TryResolve(root, requestedPath, out var full, out var relative, out error)) return false;

        var contentType = ContentTypeFor(full);
        if (contentType is null)
        {
            error = "Only .txt, .md and .markdown files can go into a knowledge base.";
            return false;
        }

        if (!File.Exists(full))
        {
            error = $"File '{requestedPath}' not found in the assistant files folder.";
            return false;
        }

        var size = new FileInfo(full).Length;
        if (size > KbManagerLimits.MaxContentBytes)
        {
            error = $"That file has {size:N0} bytes; a knowledge-base document may have at most {KbManagerLimits.MaxContentBytes:N0}.";
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(File.ReadAllBytes(full));
        }
        catch (DecoderFallbackException)
        {
            error = "That file is not valid UTF-8 text.";
            return false;
        }

        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "That file is empty.";
            return false;
        }

        file = new KbLocalFile(full, relative, text, contentType, size);
        error = string.Empty;
        return true;
    }

    public static bool TrySaveNew(
        string root, string? requestedPath, string title, string contentType, string content,
        out string relativePath, out string error)
    {
        relativePath = string.Empty;
        var extension = contentType == KbManagerLimits.Markdown ? ".md" : ".txt";

        string directory;
        string baseName;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            directory = SafeFolderPath.Canonicalize(root);
            baseName = SafeFileName(title);
        }
        else
        {
            if (!TryResolve(root, requestedPath, out var full, out _, out error)) return false;

            if (Directory.Exists(full) || requestedPath.EndsWith('/') || requestedPath.EndsWith('\\'))
            {
                directory = full;
                baseName = SafeFileName(title);
            }
            else
            {
                if (ContentTypeFor(full) is null)
                {
                    error = "A downloaded document is saved as .txt, .md or .markdown.";
                    return false;
                }

                directory = Path.GetDirectoryName(full)!;
                baseName = Path.GetFileNameWithoutExtension(full);
                extension = Path.GetExtension(full);
            }
        }

        Directory.CreateDirectory(directory);
        var canonicalRoot = SafeFolderPath.Canonicalize(root);
        for (var copy = 0; copy <= MaxNumberedCopies; copy++)
        {
            var name = copy == 0 ? baseName + extension : $"{baseName} ({copy}){extension}";
            var candidate = Path.Combine(directory, name);
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                continue;
            }

            relativePath = Path.GetRelativePath(canonicalRoot, candidate).Replace('\\', '/');
            error = string.Empty;
            return true;
        }

        error = "Every numbered copy of that name is taken; pass another path.";
        return false;
    }

    public static string SafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();

        var known = Path.GetExtension(cleaned);
        if (known.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || known.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || known.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^known.Length];

        cleaned = cleaned.Trim().TrimEnd('.', ' ');
        if (cleaned.Length > MaxFileNameChars) cleaned = cleaned[..MaxFileNameChars].TrimEnd('.', ' ');
        if (cleaned.Length == 0) return "document";

        return ReservedNames.Contains(cleaned) ? "_" + cleaned : cleaned;
    }

    private static bool TryResolve(
        string root, string? requestedPath, out string full, out string relative, out string error)
    {
        full = string.Empty;
        relative = string.Empty;

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, requestedPath, out full))
        {
            error = "That path is outside the assistant files folder.";
            return false;
        }

        if (SensitivePathGuard.IsBlocked(full, out var reason))
        {
            error = $"Refusing to use that path — {reason}.";
            return false;
        }

        relative = Path.GetRelativePath(SafeFolderPath.Canonicalize(root), full).Replace('\\', '/');
        if (IsIgnored(root, relative))
        {
            error = "That path is excluded by the folder's ignore rules (.piaignore, .gitignore or the defaults).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // The matcher applies a directory rule only to a directory path, so every ancestor is asked on its own.
    private static bool IsIgnored(string root, string relative)
    {
        var matcher = SandboxIgnore.ForRoot(root);
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
        {
            if (matcher.IsIgnored(string.Join('/', parts[..i]), isDirectory: true)) return true;
        }

        return parts.Length > 0 && matcher.IsIgnored(relative, isDirectory: false);
    }
}
