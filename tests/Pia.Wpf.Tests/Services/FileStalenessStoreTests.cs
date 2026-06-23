using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class FileStalenessStoreTests
{
    private static readonly DateTime T0 = new(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc);

    private static IFileStalenessStore NewStore() => new FileStalenessStore();

    [Fact]
    public void RecordThenCheck_SameMtime_NotStale()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();
        var path = @"C:\sandbox\file.txt";

        store.RecordRead(taskId, path, T0);

        Assert.False(store.CheckStaleness(taskId, path, T0));
    }

    [Fact]
    public void RecordThenCheck_LaterMtime_Stale()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();
        var path = @"C:\sandbox\file.txt";

        store.RecordRead(taskId, path, T0);

        Assert.True(store.CheckStaleness(taskId, path, T0.AddSeconds(1)));
    }

    [Fact]
    public void Check_UnknownKey_TreatedAsNotStale()
    {
        var store = NewStore();

        // No RecordRead was ever called for this (taskId, path): the documented default
        // is not-stale (unknown), since the guard only fires on a positive change signal.
        Assert.False(store.CheckStaleness(Guid.NewGuid(), @"C:\sandbox\never-read.txt", T0));
    }

    [Fact]
    public void Check_DifferentTaskId_TreatedAsNotStale()
    {
        var store = NewStore();
        var path = @"C:\sandbox\file.txt";
        store.RecordRead(Guid.NewGuid(), path, T0);

        // A read recorded under one task must not leak into another task's key.
        Assert.False(store.CheckStaleness(Guid.NewGuid(), path, T0.AddSeconds(1)));
    }

    [Fact]
    public void RecordRead_OverwritesPriorMtime()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();
        var path = @"C:\sandbox\file.txt";

        store.RecordRead(taskId, path, T0);
        store.RecordRead(taskId, path, T0.AddSeconds(5)); // re-read picked up the new mtime

        Assert.False(store.CheckStaleness(taskId, path, T0.AddSeconds(5)));
        Assert.True(store.CheckStaleness(taskId, path, T0));
    }

    [Fact]
    public void Key_PathComparison_IsCaseInsensitive()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();

        store.RecordRead(taskId, @"C:\sandbox\File.txt", T0);

        // Windows filesystem is case-insensitive; the key matches regardless of casing.
        Assert.True(store.CheckStaleness(taskId, @"c:\sandbox\file.txt", T0.AddSeconds(1)));
        Assert.False(store.CheckStaleness(taskId, @"c:\sandbox\file.txt", T0));
    }
}
