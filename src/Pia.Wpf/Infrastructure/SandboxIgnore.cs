using System.Diagnostics;
using System.IO;

namespace Pia.Infrastructure;

/// <summary>
/// Builds the <see cref="GitignoreMatcher"/> the file tools and the <c>@Files</c> picker use to keep
/// build/VCS/dependency noise out of listings. Patterns come from three layers, applied in order so a
/// later layer's <c>!</c> negation can re-include an earlier match:
/// <list type="number">
///   <item>the shipped defaults (an embedded ignore file — see <c>Resources/FileTools/default.piaignore</c>),</item>
///   <item>a <c>.gitignore</c> in the folder (present now that the sandbox may be a git working tree),</item>
///   <item>a <c>.piaignore</c> in the folder (the user's explicit, Pia-specific overrides).</item>
/// </list>
/// The two ignore files are read from the SAME directory the caller walks and relativizes against, so
/// anchored patterns line up. That directory is the chat's effective working directory (the sandbox
/// root narrowed by the active chat's working subpath), NOT necessarily the sandbox base — a
/// <c>.piaignore</c>/<c>.gitignore</c> must live in the working directory being browsed to take
/// effect. Only that directory's ignore files are consulted (no nested/parent ignore files); the
/// shipped defaults are depth-agnostic bare names, so the common junk is excluded at every depth.
/// </summary>
public static class SandboxIgnore
{
    public const string PiaIgnoreFileName = ".piaignore";
    public const string GitIgnoreFileName = ".gitignore";

    // Explicit LogicalName set on the <EmbeddedResource> in the csproj (avoids depending on the
    // namespace-derived manifest name, which is easy to get subtly wrong).
    private const string DefaultResourceName = "Pia.DefaultFileIgnore";

    // Bounds on a folder ignore file so a pathological/hostile .gitignore in an untrusted cloned repo
    // (hundreds of MB, or millions of lines) can't blow up memory or the per-call regex compile —
    // ForRoot runs synchronously on the @Files picker path.
    private const int MaxIgnoreChars = 128 * 1024;
    private const int MaxIgnoreLines = 2000;

    // Emergency net used only if the embedded default file cannot be read (build/packaging slip).
    // Mirrors the core of the shipped file so the picker never regresses to listing VCS/build output.
    // The embedded file remains the editable source of truth for the full set. Declared BEFORE
    // DefaultLines so it is initialized when LoadDefaults runs (static initializers run top-to-bottom).
    private static readonly string[] FallbackDefaults =
    [
        ".git/", ".svn/", ".hg/", "bin/", "obj/", "node_modules/", ".vs/", ".idea/", ".vscode/"
    ];

    private static readonly string[] DefaultLines = LoadDefaults();

    /// <summary>The shipped default patterns (embedded file, parsed once). Exposed for diagnostics/tests.</summary>
    public static IReadOnlyList<string> DefaultPatterns => DefaultLines;

    /// <summary>
    /// Returns a matcher for <paramref name="root"/>: shipped defaults plus, if present, the folder's
    /// <c>.gitignore</c> then <c>.piaignore</c>. The ignore files are re-read on every call so edits
    /// take effect without a restart; they are bounded (see <see cref="MaxIgnoreChars"/>) so this stays
    /// cheap even on the synchronous <c>@Files</c> path.
    /// </summary>
    public static GitignoreMatcher ForRoot(string root)
    {
        var lines = new List<string>(DefaultLines);
        AppendFileIfPresent(lines, root, GitIgnoreFileName);
        AppendFileIfPresent(lines, root, PiaIgnoreFileName);
        return GitignoreMatcher.FromLines(lines);
    }

    private static void AppendFileIfPresent(List<string> lines, string root, string fileName)
    {
        try
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path)) return;

            // Bounded read: at most MaxIgnoreChars characters and MaxIgnoreLines lines, so a giant or
            // single-huge-line ignore file can't allocate unboundedly or produce millions of rules.
            using var reader = new StreamReader(path);
            var buffer = new char[MaxIgnoreChars];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);

            int added = 0;
            foreach (var line in text.Split('\n'))
            {
                if (added >= MaxIgnoreLines) break;
                lines.Add(line);
                added++;
            }
        }
        catch
        {
            // A missing/locked/unreadable ignore file is non-fatal — fall back to whatever loaded.
        }
    }

    private static string[] LoadDefaults()
    {
        try
        {
            var asm = typeof(SandboxIgnore).Assembly;
            using var stream = asm.GetManifestResourceStream(DefaultResourceName);
            if (stream is null)
            {
                // The embedded resource is the real source of the defaults; a build/packaging slip
                // that drops it would otherwise silently degrade @Files back to listing .git/bin/obj.
                // FallbackDefaults keeps the feature working; a unit test asserts the resource loads.
                Debug.WriteLine($"[SandboxIgnore] Embedded default ignore resource '{DefaultResourceName}' not found; using fallback defaults.");
                return FallbackDefaults;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            // Fall back if the embedded file is present but empty/whitespace-only (e.g. truncated by a
            // bad merge) — Split never yields an empty array, so a length check alone would miss this.
            return lines.All(string.IsNullOrWhiteSpace) ? FallbackDefaults : lines;
        }
        catch
        {
            return FallbackDefaults;
        }
    }
}
