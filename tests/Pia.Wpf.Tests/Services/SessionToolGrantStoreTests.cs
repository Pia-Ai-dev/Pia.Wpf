using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The session tier is now listable and revocable, so the properties that keep it from being wider than the
/// persisted tier (ordinal, case-sensitive keys) are pinned here alongside the new list/revoke/notify surface.
/// </summary>
public class SessionToolGrantStoreTests
{
    private static readonly Guid PluginA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Revoke_RemovesTheGrant()
    {
        var sut = new SessionToolGrantStore();
        sut.Grant(PluginA, "write_file");
        Assert.True(sut.IsGranted(PluginA, "write_file"));

        sut.Revoke(PluginA, "write_file");

        Assert.False(sut.IsGranted(PluginA, "write_file"));
        Assert.Empty(sut.List());
    }

    [Fact]
    public void Revoke_LeavesOtherPluginsAlone()
    {
        var sut = new SessionToolGrantStore();
        sut.Grant(PluginA, "write_file");
        sut.Grant(PluginB, "write_file");

        sut.Revoke(PluginA, "write_file");

        Assert.False(sut.IsGranted(PluginA, "write_file"));
        Assert.True(sut.IsGranted(PluginB, "write_file"));
    }

    [Fact]
    public void Changed_FiresOnGrantAndOnRevoke()
    {
        var sut = new SessionToolGrantStore();
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.Grant(PluginA, "write_file");
        Assert.Equal(1, fired);

        sut.Revoke(PluginA, "write_file");
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Changed_DoesNotFireWhenNothingChanged()
    {
        var sut = new SessionToolGrantStore();
        sut.Grant(PluginA, "write_file");

        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.Grant(PluginA, "write_file");     // already granted
        sut.Revoke(PluginB, "write_file");    // never granted
        sut.Grant(PluginA, "   ");
        sut.Revoke(PluginA, "   ");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void List_RecordsTheGrantTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var sut = new SessionToolGrantStore();

        sut.Grant(PluginA, "write_file");

        var row = Assert.Single(sut.List());
        Assert.Equal(PluginA, row.PluginId);
        Assert.Equal("write_file", row.ToolName);
        Assert.InRange(row.GrantedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Grant_Repeated_KeepsTheOriginalTimestamp()
    {
        var sut = new SessionToolGrantStore();
        sut.Grant(PluginA, "write_file");
        var first = Assert.Single(sut.List()).GrantedAt;

        await Task.Delay(20, TestContext.Current.CancellationToken);
        sut.Grant(PluginA, "write_file");

        Assert.Equal(first, Assert.Single(sut.List()).GrantedAt);
    }

    [Fact]
    public void Keys_AreOrdinalAndCaseSensitive()
    {
        var sut = new SessionToolGrantStore();
        sut.Grant(PluginA, "write_file");

        Assert.False(sut.IsGranted(PluginA, "Write_File"));
        Assert.False(sut.IsGranted(PluginA, "WRITE_FILE"));

        // A case-folding key would also make this revoke land on the lower-case grant.
        sut.Revoke(PluginA, "Write_File");
        Assert.True(sut.IsGranted(PluginA, "write_file"));
    }

    [Fact]
    public void List_ReturnsTheNameExactlyAsGranted()
    {
        var sut = new SessionToolGrantStore();
        sut.Grant(PluginA, "Git_Commit");

        Assert.Equal("Git_Commit", Assert.Single(sut.List()).ToolName);
    }

    [Fact]
    public void BlankToolName_IsIgnoredEverywhere()
    {
        var sut = new SessionToolGrantStore();

        sut.Grant(PluginA, "  ");

        Assert.Empty(sut.List());
        Assert.False(sut.IsGranted(PluginA, "  "));
        sut.Revoke(PluginA, "  ");
    }

    [Fact]
    public void Changed_IsRaisedOutsideTheLock()
    {
        var sut = new SessionToolGrantStore();
        var reentered = false;

        // Monitor is re-entrant on the SAME thread, so re-entering from the handler's own thread would pass
        // even if the raise sat inside the lock. Another thread is what discriminates.
        sut.Changed += (_, _) =>
        {
            reentered = Task.Run(() => sut.List()).Wait(TimeSpan.FromSeconds(5));
        };

        sut.Grant(PluginA, "write_file");

        Assert.True(reentered, "a Changed handler could not read the store from another thread, so the event " +
                               "is being raised while the store lock is held.");
    }
}
