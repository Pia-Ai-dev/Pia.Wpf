namespace Pia.Services.Interfaces;

/// <summary>
/// Outcome of a single <see cref="IVaultMigrationRunner.RunAsync"/> invocation.
/// <see cref="Skipped"/> is <c>true</c> when the guard short-circuited (already migrated, or the vault
/// is already populated on this device) — in that case the count fields are all zero.
/// <see cref="RowsMigrated"/> is the number of legacy <c>Memories</c> rows processed;
/// <see cref="RecordsWritten"/> is the number of vault records written via the write path (a single
/// <c>contact_list</c> row can yield several records); <see cref="Archived"/> is the number of original
/// rows snapshotted under <c>memory/.archive/</c> for recoverability.
/// </summary>
public record MigrationReport(bool Skipped, int RowsMigrated, int RecordsWritten, int Archived);

/// <summary>
/// One-shot, idempotent migration of the legacy SQLite <c>Memories</c> table into the on-disk memory
/// vault (format spec v1). Records are written THROUGH the deterministic upsert write path so clear
/// duplicates merge on the way in (dedup-on-write); every original row is archived first so nothing is
/// lost. The legacy table is left intact (the drop + CRUD retirement is a deferred follow-up task).
/// </summary>
public interface IVaultMigrationRunner
{
    /// <summary>
    /// Run the migration if it has not already happened on this device. Idempotent: a second call after
    /// a successful run returns <see cref="MigrationReport.Skipped"/> = <c>true</c> and writes nothing.
    /// </summary>
    Task<MigrationReport> RunAsync();
}
