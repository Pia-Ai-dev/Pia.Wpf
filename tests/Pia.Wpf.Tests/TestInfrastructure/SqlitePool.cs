using Microsoft.Data.Sqlite;

namespace Pia.Tests.TestInfrastructure;

internal static class SqlitePool
{
    /// <summary>Returns ONE file's pooled handles, so a disposed connection really closes and frees the WAL side
    /// files. Scoped because process-global <c>ClearAllPools</c> also disposed handles a parallel test class had
    /// just rented, surfacing there as <c>ObjectDisposedException</c> on <c>SQLitePCL.sqlite3</c>.</summary>
    public static void ClearFor(string connectionString)
    {
        using var handle = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(handle);
    }
}
