using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Policy can hide built-in personas from the picker. The ids stay reserved, and the
/// never-null contract on <see cref="PersonaService.ResolveActiveAsync"/> outranks the block-list.</summary>
public class PersonaBlockListTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly TestSettingsService _settings = new();
    private readonly PersonaService _service;
    private readonly string _tmpDir;

    public PersonaBlockListTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaPersonaBlock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _service = new PersonaService(_ctx, NullLogger<PersonaService>.Instance, _deleteTracker, _settings);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task NoBlockList_ListsEveryBuiltIn()
    {
        var personas = await _service.GetPersonasAsync();

        Assert.Equal(BuiltInPersonas.All.Count, personas.Count(p => p.IsBuiltIn));
    }

    [Fact]
    public async Task BlockedByKey_IsHiddenFromTheList()
    {
        _settings.Settings.BlockedBuiltInPersonas = ["ExperiencedCoder"];

        var personas = await _service.GetPersonasAsync();

        Assert.DoesNotContain(personas, p => p.Id == BuiltInPersonas.ExperiencedCoderId);
        Assert.Contains(personas, p => p.Id == BuiltInPersonas.PiaPersonalId);
    }

    [Fact]
    public async Task BlockedByGuid_IsHiddenFromTheList()
    {
        _settings.Settings.BlockedBuiltInPersonas = [BuiltInPersonas.MarketingWriterId.ToString()];

        var personas = await _service.GetPersonasAsync();

        Assert.DoesNotContain(personas, p => p.Id == BuiltInPersonas.MarketingWriterId);
    }

    [Fact]
    public async Task UnknownEntry_IsIgnored()
    {
        _settings.Settings.BlockedBuiltInPersonas = ["NoSuchPersona", ""];

        var personas = await _service.GetPersonasAsync();

        Assert.Equal(BuiltInPersonas.All.Count, personas.Count(p => p.IsBuiltIn));
    }

    [Fact]
    public async Task BlockedBuiltIn_IsStillReservedSoItCannotBeReCreated()
    {
        // The list filter must not reach _builtInIds: that set is what refuses an update/delete on a
        // built-in id, and a hidden built-in is still a built-in.
        _settings.Settings.BlockedBuiltInPersonas = ["ExperiencedCoder"];

        await Assert.ThrowsAnyAsync<Exception>(() => _service.UpdatePersonaAsync(new Persona
        {
            Id = BuiltInPersonas.ExperiencedCoderId,
            Name = "Hijacked",
            SystemPrompt = "prompt",
        }));
    }

    [Fact]
    public async Task ResolveActive_FallsBackToAnotherBuiltIn_WhenTheModeDefaultIsBlocked()
    {
        _settings.Settings.BlockedBuiltInPersonas = ["PiaPersonal"];

        var active = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);

        Assert.NotNull(active);
        Assert.NotEqual(BuiltInPersonas.PiaPersonalId, active.Id);
    }

    [Fact]
    public async Task ResolveActive_ReturnsTheModeDefault_EvenWhenEveryBuiltInIsBlocked()
    {
        // Never returning null outranks honouring the block-list — the assistant must still have a persona.
        _settings.Settings.BlockedBuiltInPersonas = BuiltInPersonas.ByKey.Keys.ToList();

        var active = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Business);

        Assert.Equal(BuiltInPersonas.PiaBusinessId, active.Id);
    }

    [Fact]
    public async Task ResolveActive_TreatsABlockedSelectionAsUnknown()
    {
        _settings.Settings.BlockedBuiltInPersonas = ["ExperiencedCoder"];
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, BuiltInPersonas.ExperiencedCoderId);

        var active = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);

        Assert.Equal(BuiltInPersonas.PiaPersonalId, active.Id);
    }

    [Fact]
    public void ResolveBlockedBuiltInIds_ReadsKeysGuidsAndSkipsJunk()
    {
        var settings = new AppSettings
        {
            BlockedBuiltInPersonas =
                ["PiaBusiness", BuiltInPersonas.FinancialExpertId.ToString(), "nonsense", "  "],
        };

        var blocked = PersonaService.ResolveBlockedBuiltInIds(settings);

        Assert.Equal(2, blocked.Count);
        Assert.Contains(BuiltInPersonas.PiaBusinessId, blocked);
        Assert.Contains(BuiltInPersonas.FinancialExpertId, blocked);
    }

    [Theory]
    [InlineData("PiaBusiness", true)]
    [InlineData("piabusiness", true)]
    [InlineData("  ExplainItSimply  ", true)]
    [InlineData("NotAPersona", false)]
    [InlineData("", false)]
    public void Resolve_MatchesKeysCaseInsensitivelyAndRejectsUnknowns(string entry, bool expected)
    {
        Assert.Equal(expected, BuiltInPersonas.Resolve(entry) is not null);
    }

    [Fact]
    public void Resolve_RejectsAGuidThatIsNotABuiltIn()
    {
        Assert.Null(BuiltInPersonas.Resolve(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void ByKey_CoversEveryBuiltIn()
    {
        Assert.Equal(BuiltInPersonas.All.Count, BuiltInPersonas.ByKey.Count);
        foreach (var builtIn in BuiltInPersonas.All)
            Assert.Contains(Guid.Parse(builtIn.Id), BuiltInPersonas.ByKey.Values);
    }

    private sealed class TestSettingsService : ISettingsService
    {
#pragma warning disable CS0067
        public event EventHandler<AppSettings>? SettingsChanged;
#pragma warning restore CS0067
        public AppSettings Settings { get; } = new();
        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }
}
