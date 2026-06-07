namespace Pia.Services.Interfaces;

/// <summary>
/// The inner sync envelope for a Pia-managed vault file (memory-vault format spec §11, contract C5):
/// <c>{ path, content }</c>. <see cref="Path"/> is the vault-relative path of the file;
/// <see cref="Content"/> is the entire file content (frontmatter + body), byte-for-byte.
/// <para>
/// When E2EE is active this object is the plaintext that gets AES-GCM-encrypted into
/// <c>SyncMemory.EncryptedPayload</c> (the server never sees the path); when E2EE is off the same
/// path+content travel in the plaintext <c>SyncMemory.Path</c>/<c>Data</c> fields. The server row is
/// keyed by the file's frontmatter <c>id</c> GUID, not by path, so a file can be renamed/moved without
/// orphaning its server row.
/// </para>
/// </summary>
public sealed record VaultSyncPayload(string Path, string Content);
