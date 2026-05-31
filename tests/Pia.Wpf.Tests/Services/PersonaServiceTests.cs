using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Coverage for <see cref="PersonaService"/>: built-in ∪ user merge, built-in immutability,
/// delete tracking, and active-persona resolution with the operating-mode fallback (contract §7).
/// </summary>
public class PersonaServiceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly TestSettingsService _settings;
    private readonly PersonaService _service;
    private readonly string _tmpDir;
    private readonly List<Guid> _created = [];

    public PersonaServiceTests()
    {
        _ctx = new SqliteContext();
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaPersonaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _settings = new TestSettingsService();
        _service = new PersonaService(_ctx, NullLogger<PersonaService>.Instance, _deleteTracker, _settings);
    }

    private async Task<Persona> AddUserPersonaAsync(string name = "TEST_Persona")
    {
        var persona = await _service.AddPersonaAsync(new Persona
        {
            Name = name,
            SystemPrompt = "You are a test persona.",
            ToolScope = PersonaToolScope.Full,
        });
        _created.Add(persona.Id);
        return persona;
    }

    [Fact]
    public async Task GetPersonasAsync_MergesBuiltInsFirstThenUser()
    {
        var user = await AddUserPersonaAsync();

        var all = await _service.GetPersonasAsync();

        var builtIn = all.FirstOrDefault(p => p.Id == BuiltInPersonas.PiaPersonalId);
        Assert.NotNull(builtIn);
        Assert.True(builtIn!.IsBuiltIn);

        var userRow = all.FirstOrDefault(p => p.Id == user.Id);
        Assert.NotNull(userRow);
        Assert.False(userRow!.IsBuiltIn);

        // Built-ins are listed before user personas.
        var lastBuiltInIndex = all.Select((p, i) => (p, i)).Where(t => t.p.IsBuiltIn).Max(t => t.i);
        var userIndex = all.Select((p, i) => (p, i)).First(t => t.p.Id == user.Id).i;
        Assert.True(userIndex > lastBuiltInIndex);
    }

    [Fact]
    public async Task UpdatePersonaAsync_BuiltIn_Throws()
    {
        var builtIn = (await _service.GetPersonasAsync()).First(p => p.IsBuiltIn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdatePersonaAsync(builtIn));
    }

    [Fact]
    public async Task DeletePersonaAsync_BuiltIn_IsNoOpAndNotTracked()
    {
        await _service.DeletePersonaAsync(BuiltInPersonas.PiaBusinessId);

        var all = await _service.GetPersonasAsync();
        Assert.Contains(all, p => p.Id == BuiltInPersonas.PiaBusinessId);

        var pending = _deleteTracker.GetPendingDeletes();
        Assert.False(pending.TryGetValue("personas", out var ids) && ids.Contains(BuiltInPersonas.PiaBusinessId));
    }

    [Fact]
    public async Task DeletePersonaAsync_User_RemovesAndTracks()
    {
        var user = await AddUserPersonaAsync();

        await _service.DeletePersonaAsync(user.Id);

        var all = await _service.GetPersonasAsync();
        Assert.DoesNotContain(all, p => p.Id == user.Id);

        var pending = _deleteTracker.GetPendingDeletes();
        Assert.True(pending.TryGetValue("personas", out var ids));
        Assert.Contains(user.Id, ids!);
    }

    [Fact]
    public async Task ResolveActiveAsync_NoSelection_FallsBackToOperatingModeBuiltIn()
    {
        var personal = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);
        Assert.Equal(BuiltInPersonas.PiaPersonalId, personal.Id);

        var business = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Business);
        Assert.Equal(BuiltInPersonas.PiaBusinessId, business.Id);
    }

    [Fact]
    public async Task ResolveActiveAsync_UnknownSelection_FallsBackToBuiltIn()
    {
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, Guid.NewGuid());

        var resolved = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);

        Assert.Equal(BuiltInPersonas.PiaPersonalId, resolved.Id);
    }

    [Fact]
    public async Task ResolveActiveAsync_ValidSelection_ReturnsSelectedPersona()
    {
        var user = await AddUserPersonaAsync();
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, user.Id);

        var resolved = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);

        Assert.Equal(user.Id, resolved.Id);
        Assert.False(resolved.IsBuiltIn);
    }

    public void Dispose()
    {
        foreach (var id in _created)
        {
            try { _service.DeletePersonaAsync(id).GetAwaiter().GetResult(); }
            catch { /* best-effort cleanup of the shared test database */ }
        }
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
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
