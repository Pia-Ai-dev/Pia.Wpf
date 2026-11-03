using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>A store read moves an unparseable file aside, so the pending wipe must run first to still refuse it.</summary>
public class LocalDataWipeOrderTests
{
    private static readonly string BootstrapperPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf", "Bootstrapper.cs"));

    private static readonly Regex StoreAccess = new(
        @"GetRequiredService<I(Settings|Policy|Provider|Template)Service>|\.Get(Settings|Policy|Providers|Templates)Async\(");

    [Fact]
    public void PendingWipe_RunsBeforeAnyStoreIsRead()
    {
        var source = File.ReadAllText(BootstrapperPath);
        var wipe = source.IndexOf("LocalDataResetStartup.RunIfPendingAsync(", StringComparison.Ordinal);
        Assert.True(wipe >= 0, "Bootstrapper no longer runs the pending wipe");

        var firstRead = StoreAccess.Match(source);
        Assert.True(firstRead.Success, "no store read found in Bootstrapper");
        Assert.True(wipe < firstRead.Index, $"'{firstRead.Value}' runs before the pending wipe");
    }
}
