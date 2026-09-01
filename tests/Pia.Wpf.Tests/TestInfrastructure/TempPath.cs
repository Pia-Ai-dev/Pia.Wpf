using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Pia.Tests.TestInfrastructure;

internal static class TempPath
{
    private static readonly ConcurrentBag<string> Stuck = [];

    static TempPath() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Report();

    /// <summary>Deletes a test temp directory for real — a plain recursive delete leaks it, because pooled SQLite
    /// handles hold the <c>.db</c> open and git marks everything under <c>.git/objects</c> read-only.</summary>
    public static void Remove(string? directory, [CallerFilePath] string? caller = null)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        Retry(directory, caller, attempt =>
        {
            ReleasePooledDatabases(directory);

            // Only git dirs carry read-only files, so the second walk is not worth paying for on the happy path.
            if (attempt > 0)
            {
                ClearReadOnlyUnder(directory);
            }

            Directory.Delete(directory, recursive: true);
        });
    }

    public static void RemoveFile(string? file, [CallerFilePath] string? caller = null)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return;
        }

        Retry(file, caller, _ =>
        {
            ReleasePooledDatabase(file);
            ClearReadOnlyFile(file);
            File.Delete(file);
        });
    }

    private static void ReleasePooledDatabases(string directory)
    {
        foreach (var db in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            ReleasePooledDatabase(db);
        }
    }

    private static void ClearReadOnlyUnder(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ClearReadOnlyFile(file);
        }
    }

    private static void ReleasePooledDatabase(string file)
    {
        if (file.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            SqlitePool.ClearFor($"Data Source={file}");
        }
    }

    private static void ClearReadOnlyFile(string file)
    {
        try
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (FileNotFoundException)
        {
            // Clearing the SQLite pool drops the -wal/-shm siblings the enumeration already listed.
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Re-clears the pool on every attempt: a connection that was rented when the first clear ran is
    /// still holding the file, and only goes back to the pool — where a later clear can dispose it — afterwards.</summary>
    private static void Retry(string target, string? caller, Action<int> delete)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                delete(attempt);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4)
                {
                    Stuck.Add($"{Path.GetFileName(caller) ?? "?"}  {target}");
                    return;
                }

                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    /// <summary>Named at the end of the run rather than thrown: a still-held handle is a real defect in the test
    /// that owns it, but failing an otherwise-green test for it only hides the list.</summary>
    private static void Report()
    {
        var stuck = Stuck.ToArray();
        if (stuck.Length == 0)
        {
            return;
        }

        Console.WriteLine($"[TempPath] {stuck.Length} temp path(s) survived teardown — a handle was still open:");
        foreach (var path in stuck.Order(StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[TempPath]   {path}");
        }
    }
}
