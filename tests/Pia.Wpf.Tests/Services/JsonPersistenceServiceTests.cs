using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Services;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class JsonPersistenceServiceTests : IDisposable
{
    private const string Malformed = "{ \"name\": \"secret-content\", ";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 13, 45, 7, TimeSpan.Zero);
    private const string AsideName = "store.json.unreadable-20261027T134507Z";

    private readonly string _dir;

    public JsonPersistenceServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaJsonStore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TempPath.Remove(_dir);

    private string StorePath => Path.Combine(_dir, "store.json");

    [Fact]
    public async Task Load_MalformedFile_MovesItAsideAndReturnsDefaults()
    {
        File.WriteAllText(StorePath, Malformed);
        var store = new Store(_dir);

        var loaded = await store.Load();

        Assert.Equal("default", loaded.Name);
        Assert.False(File.Exists(StorePath));
        Assert.Equal(Malformed, File.ReadAllText(Path.Combine(_dir, AsideName)));
        var warning = Assert.Single(store.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("store.json", warning.Message);
        Assert.Contains(AsideName, warning.Message);
        Assert.DoesNotContain("secret-content", warning.Message);
        Assert.DoesNotContain(_dir, warning.Message);
    }

    [Fact]
    public async Task Save_AfterMovingAside_LeavesTheAsideCopyUntouched()
    {
        File.WriteAllText(StorePath, Malformed);
        var store = new Store(_dir);
        await store.Load();

        await store.Save(new Payload { Name = "saved" });

        Assert.Equal(Malformed, File.ReadAllText(Path.Combine(_dir, AsideName)));
        Assert.Contains("saved", File.ReadAllText(StorePath));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task Load_AsideNameTaken_PicksTheNextFreeSuffix()
    {
        File.WriteAllText(Path.Combine(_dir, AsideName), "earlier");
        File.WriteAllText(StorePath, Malformed);

        await new Store(_dir).Load();

        Assert.Equal("earlier", File.ReadAllText(Path.Combine(_dir, AsideName)));
        Assert.Equal(Malformed, File.ReadAllText(Path.Combine(_dir, AsideName + "-2")));
    }

    [Fact]
    public async Task Load_FileCannotBeMoved_StillReturnsDefaultsAndLogs()
    {
        File.WriteAllText(StorePath, Malformed);
        var store = new Store(_dir);

        Payload loaded;
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            loaded = await store.Load();
        }

        Assert.Equal("default", loaded.Name);
        Assert.True(File.Exists(StorePath));
        Assert.False(File.Exists(Path.Combine(_dir, AsideName)));
        var warning = Assert.Single(store.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("could not be moved aside", warning.Message);
    }

    public sealed class Payload
    {
        public string Name { get; set; } = "default";
    }

    private sealed class Store(string directory) : JsonPersistenceService<Payload>
    {
        public CapturingLogger<Store> Log { get; } = new();

        protected override string DirectoryPath => directory;
        protected override string FileName => "store.json";
        protected override ILogger Logger => Log;
        protected override TimeProvider Clock { get; } = new FixedClock(Now);
        protected override Payload CreateDefault() => new();

        public Task<Payload> Load() => LoadAsync();
        public Task Save(Payload data) => SaveAsync(data);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
