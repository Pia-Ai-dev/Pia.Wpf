using Pia.Models.Vault;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// File-level access to the on-disk memory vault (format spec v1). All edits go through here so the
/// atomic write-tmp-then-rename and the byte-range section splice (spec §3.1) are applied uniformly.
/// </summary>
public interface IVaultStore
{
    /// <summary>Absolute path of the vault root directory.</summary>
    string Root { get; }

    /// <summary>Parse the file at a vault-relative path, or <c>null</c> if it does not exist.</summary>
    Task<VaultDocument?> ReadAsync(string relativePath);

    /// <summary>Write the exact <paramref name="content"/> atomically (tmp + rename), creating dirs.</summary>
    Task WriteAtomicAsync(string relativePath, string content);

    /// <summary>Replace only the body of the section with <paramref name="slug"/> via byte-range splice (§3.1).</summary>
    Task SpliceSectionAsync(string relativePath, string slug, string newBody);

    /// <summary>Vault-relative paths of <c>*.md</c> files matching the glob under <see cref="Root"/>, sorted.</summary>
    Task<IReadOnlyList<string>> EnumerateAsync(string globUnderRoot);

    /// <summary>Delete the file at a vault-relative path if it exists.</summary>
    Task DeleteAsync(string relativePath);
}
