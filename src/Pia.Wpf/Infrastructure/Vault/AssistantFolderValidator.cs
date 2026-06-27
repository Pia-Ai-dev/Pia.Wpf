using System;
using System.IO;
using System.Linq;
using Pia.Infrastructure; // SafeFolderPath, SensitivePathGuard

namespace Pia.Infrastructure.Vault;

public enum FolderValidation
{
    Ok,
    OutsideUserProfile,   // Rule 1
    BlockedPath,          // system / Pia-data / credential dir
    NestedInCurrent,      // would copy a tree into itself
    NotEmpty,             // existing target already has content (merge ambiguity / rollback risk)
    Invalid,              // unusable path
}

/// <summary>
/// Grounds a user-picked assistant files folder against the structural rules, reusing the same
/// secure path primitives the file tools use (canonicalization + trailing-separator containment +
/// the sensitive-path denylist). Rule 2 (vault under the folder) is structural and not checked here —
/// the vault is always <c>&lt;folder&gt;\Vault</c>.
/// </summary>
public static class AssistantFolderValidator
{
    public static FolderValidation Validate(string candidate, string? currentFolder)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return FolderValidation.Invalid;

        string canonical, profile;
        try
        {
            canonical = CanonicalizeExistingOrLexical(candidate);
            profile = CanonicalizeExistingOrLexical(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch { return FolderValidation.Invalid; }

        // Rule 1: strictly under %USERPROFILE% (trailing-separator-aware, case-insensitive).
        var profileWithSep = SafeFolderPath.WithTrailingSeparator(profile);
        if (!canonical.StartsWith(profileWithSep, StringComparison.OrdinalIgnoreCase))
            return FolderValidation.OutsideUserProfile;

        // Never a system / Pia-data / credential dir.
        if (SensitivePathGuard.IsBlocked(canonical, out _))
            return FolderValidation.BlockedPath;

        // No copying a tree into itself / its own vault.
        if (!string.IsNullOrWhiteSpace(currentFolder))
        {
            var curr = CanonicalizeExistingOrLexical(currentFolder!);
            var currWithSep = SafeFolderPath.WithTrailingSeparator(curr);
            if (canonical.Equals(curr, StringComparison.OrdinalIgnoreCase))
                return FolderValidation.Ok; // same folder = no-op move, allowed
            if (canonical.StartsWith(currWithSep, StringComparison.OrdinalIgnoreCase))
                return FolderValidation.NestedInCurrent;
        }

        // An existing, non-empty target makes the merge ambiguous and the rollback unsafe (a failed
        // verify could delete the user's pre-existing files). Require an empty/new folder — the Win32
        // folder picker has a "New folder" button, so this is easy to satisfy.
        try
        {
            if (Directory.Exists(canonical) &&
                Directory.EnumerateFileSystemEntries(canonical).Any())
                return FolderValidation.NotEmpty;
        }
        catch { return FolderValidation.Invalid; }

        return FolderValidation.Ok;
    }

    private static string CanonicalizeExistingOrLexical(string path)
    {
        var full = Path.GetFullPath(path);
        return Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : full;
    }
}
