using System;
using System.IO;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>A failed clear may leave the old account signed in, so its scheduled jobs must not start.</summary>
public class PendingResetStartupGateTests
{
    private static readonly string AppPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf", "App.xaml.cs"));

    [Theory]
    [InlineData("scheduledJobService.StartAsync(")]
    [InlineData("chatSyncService.StartAsync(")]
    [InlineData("chatRetentionService.StartAsync(")]
    public void AccountBoundService_StartsOnlyWithoutAPendingClear(string start)
    {
        var source = File.ReadAllText(AppPath);
        var call = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(call >= 0, $"App no longer calls '{start}'");

        var guard = source.LastIndexOf("if (!resetPending)", call, StringComparison.Ordinal);
        Assert.True(guard >= 0, $"'{start}' runs before any pending-clear check");
        Assert.True(call < ClosingBrace(source, source.IndexOf('{', guard)), $"'{start}' runs outside the pending-clear check");
    }

    private static int ClosingBrace(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
