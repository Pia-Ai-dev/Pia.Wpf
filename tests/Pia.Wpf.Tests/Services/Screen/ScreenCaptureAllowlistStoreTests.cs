using System.IO;
using Pia.Services.Screen;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Screen;

public sealed class ScreenCaptureAllowlistStoreTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempPath.Remove(_tmpDir);

    private ScreenCaptureAllowlistStore Store() => new(_tmpDir);

    [Fact]
    public async Task AddAsync_PersistsAndReloadsFromAFreshInstance()
    {
        var added = await Store().AddAsync("outlook", "Inbox");

        Assert.NotNull(added);
        Assert.True(File.Exists(Path.Combine(_tmpDir, "screen-capture-allowlist.json")));

        var reloaded = Assert.Single(await Store().ListAsync());
        Assert.Equal(added!.Id, reloaded.Id);
        Assert.Equal("outlook", reloaded.ProcessName);
        Assert.Equal("Inbox", reloaded.TitleContains);
    }

    /// <summary>The row shows what actually matches, so the stored name is the normalised one.</summary>
    [Fact]
    public async Task AddAsync_NormalisesTheProcessName_AndKeepsTheTrimmedPattern()
    {
        var added = await Store().AddAsync("  Outlook.exe  ", "  Inbox  ");

        Assert.Equal("Outlook", added!.ProcessName);
        Assert.Equal("Inbox", added.TitleContains);
    }

    [Fact]
    public async Task AddAsync_RejectsABlankProcess_ReturnsNull_WritesNothing()
    {
        var store = Store();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        Assert.Null(await store.AddAsync("   ", "Inbox"));
        Assert.Null(await store.AddAsync(null, null));

        Assert.Empty(await store.ListAsync());
        Assert.Equal(0, raised);
        Assert.False(File.Exists(Path.Combine(_tmpDir, "screen-capture-allowlist.json")));
    }

    [Fact]
    public async Task AddAsync_IgnoresADuplicate_CaseAndExtensionBlind()
    {
        var store = Store();
        var raised = 0;
        Assert.NotNull(await store.AddAsync("outlook", "Inbox"));
        store.Changed += (_, _) => raised++;

        Assert.Null(await store.AddAsync("OUTLOOK.EXE", " inbox "));

        Assert.Single(await store.ListAsync());
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task RemoveAsync_ReturnsTrueOnce_ThenFalse_AndRaisesChangedOnlyWhenSomethingWasRemoved()
    {
        var store = Store();
        var added = await store.AddAsync("outlook", "");
        var raised = 0;
        store.Changed += (_, _) => raised++;

        Assert.True(await store.RemoveAsync(added!.Id));
        Assert.Equal(1, raised);

        Assert.False(await store.RemoveAsync(added.Id));
        Assert.Equal(1, raised);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Changed_IsRaisedOnAdd()
    {
        var store = Store();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        await store.AddAsync("outlook", "");

        Assert.Equal(1, raised);
    }

    /// <summary>An empty pattern means "any single window of this program" — the exactly-one rule still applies.</summary>
    [Fact]
    public async Task AddAsync_AcceptsABlankTitlePattern()
    {
        var added = await Store().AddAsync("outlook", null);

        Assert.NotNull(added);
        Assert.Equal(string.Empty, added!.TitleContains);
    }

    /// <summary>The cached state is the store's own object; handing it out would let a caller edit past the gate.</summary>
    [Fact]
    public async Task ListAsync_HandsBackACopy()
    {
        var store = Store();
        await store.AddAsync("outlook", "");

        var first = await store.ListAsync();
        Assert.NotSame(first, await store.ListAsync());
    }
}
