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
/// Coverage for <see cref="PersonaService"/>: built-in ∪ managed ∪ user merge, built-in and managed
/// immutability, delete tracking (including the pin that a managed id never becomes a push tombstone),
/// the replace-all managed store with its withdrawal latch, and active-persona resolution with the
/// operating-mode fallback (contract §7).
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
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaPersonaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        // An EXPLICIT temp database, never the parameterless ctor's %LOCALAPPDATA%\Pia\history.db: the
        // managed-store tests below call ReplaceManagedPersonasAsync, whose first statement is
        // `DELETE FROM ManagedPersonas` — against a real profile that wipes every admin-published persona
        // the developer is signed in to, and the first-run latch in the real settings.json means the next
        // sync echoes a current catalogVersion and never re-fetches them.
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
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
    public async Task AddAndGetPersona_RoundTripsOutputFormat()
    {
        var outputFormat = "- Lead with the answer.\n- Use code blocks for code.";
        var added = await _service.AddPersonaAsync(new Persona
        {
            Name = "TEST_OutputFormat",
            SystemPrompt = "You are a test persona.",
            OutputFormat = outputFormat,
            ToolScope = PersonaToolScope.Full,
        });
        _created.Add(added.Id);

        var fetched = await _service.GetPersonaAsync(added.Id);

        Assert.NotNull(fetched);
        Assert.Equal(outputFormat, fetched!.OutputFormat);

        // A persona with no output format round-trips as null (substrate default applies at prompt time).
        var plain = await AddUserPersonaAsync("TEST_NoOutputFormat");
        var fetchedPlain = await _service.GetPersonaAsync(plain.Id);
        Assert.Null(fetchedPlain!.OutputFormat);
    }

    [Fact]
    public async Task UpdatePersonaAsync_PersistsOutputFormat()
    {
        var user = await AddUserPersonaAsync();
        user.OutputFormat = "- Be terse.";

        await _service.UpdatePersonaAsync(user);

        var fetched = await _service.GetPersonaAsync(user.Id);
        Assert.Equal("- Be terse.", fetched!.OutputFormat);
    }

    [Fact]
    public async Task AddAndUpdatePersona_RoundTripsModelType()
    {
        var added = await _service.AddPersonaAsync(new Persona
        {
            Name = "TEST_ModelType",
            SystemPrompt = "You are a test persona.",
            ModelType = "fast",
            ToolScope = PersonaToolScope.Full,
        });
        _created.Add(added.Id);

        var fetched = await _service.GetPersonaAsync(added.Id);
        Assert.Equal("fast", fetched!.ModelType);

        fetched.ModelType = null;
        await _service.UpdatePersonaAsync(fetched);

        // A blanked hint reads back as the default type, not as a stale value and not as null.
        var cleared = await _service.GetPersonaAsync(added.Id);
        Assert.Equal(Persona.DefaultModelType, cleared!.ModelType);

        // A persona that never set the hint reads back as the default type too.
        var plain = await AddUserPersonaAsync("TEST_NoModelType");
        Assert.Equal(Persona.DefaultModelType, (await _service.GetPersonaAsync(plain.Id))!.ModelType);
    }

    [Fact]
    public async Task BuiltInPersonas_HaveTheDefaultModelType()
    {
        var builtIns = (await _service.GetPersonasAsync()).Where(p => p.IsBuiltIn).ToList();

        Assert.NotEmpty(builtIns);
        Assert.All(builtIns, p => Assert.Equal(Persona.DefaultModelType, p.ModelType));
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

    // ---- managed personas: the admin-published, replace-all pull channel ----

    private static Persona NewManagedPersona(string name, Guid? id = null, DateTime? createdAt = null)
    {
        var stamp = createdAt ?? DateTime.UtcNow;
        return new Persona
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            SystemPrompt = "You are an admin-published persona.",
            ToolScope = PersonaToolScope.Full,
            CreatedAt = stamp,
            UpdatedAt = stamp,
        };
    }

    /// <summary>
    /// Seeds the managed store through the only writer there is. Replace-all semantics mean every seed starts
    /// from an empty store, on top of this instance's own temp database.
    /// </summary>
    private async Task<Persona[]> AddManagedPersonasAsync(params Persona[] personas)
    {
        await _service.ReplaceManagedPersonasAsync(personas);
        return personas;
    }

    /// <summary>
    /// Direct read of the pushed <c>Personas</c> table. Several assertions below are about what is on DISK
    /// rather than what the merged list shows — a suppressed user row must survive, and a managed row must
    /// never appear here at all (this table is the push source).
    /// </summary>
    private bool UserPersonaRowExists(Guid id)
    {
        using var command = _ctx.GetConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Personas WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    [Fact]
    public async Task GetPersonasAsync_OrdersBuiltInsThenManagedThenUser()
    {
        var user = await AddUserPersonaAsync("TEST_UserForOrder");
        var managed = await AddManagedPersonasAsync(
            NewManagedPersona("TEST_ManagedOrderA", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewManagedPersona("TEST_ManagedOrderB", createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));

        var all = await _service.GetPersonasAsync();

        // Built-ins occupy the head, so the managed block starts at exactly the built-in count — asserted by
        // index, because the index order is the contract the picker relies on.
        var builtInCount = all.Count(p => p.IsBuiltIn);
        Assert.Equal(managed[0].Id, all[builtInCount].Id);
        Assert.Equal(managed[1].Id, all[builtInCount + 1].Id);
        Assert.True(all[builtInCount].IsManaged);

        var userIndex = all.Select((p, i) => (p, i)).First(t => t.p.Id == user.Id).i;
        Assert.True(userIndex > builtInCount + 1);
    }

    [Fact]
    public async Task ReplaceManagedPersonasAsync_MaterializesManagedFlags()
    {
        var seeded = await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedFlags"));

        var stored = await _service.GetManagedPersonasAsync();

        Assert.Single(stored);
        Assert.Equal(seeded[0].Id, stored[0].Id);
        Assert.True(stored[0].IsManaged);
        Assert.False(stored[0].IsBuiltIn);
        Assert.True(stored[0].IsReadOnly);
    }

    [Fact]
    public async Task ManagedPersona_WithoutModelType_ReadsBackTheDefault()
    {
        // The managed wire DTO has no ModelType field, so every managed row lands in the store with a
        // blank type; the read path must still hand chat a routable value.
        await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedModelType"));

        var stored = await _service.GetManagedPersonasAsync();

        Assert.Equal(Persona.DefaultModelType, Assert.Single(stored).ModelType);
    }

    [Fact]
    public async Task ReplaceManagedPersonasAsync_IsReplaceAll_NotMerge()
    {
        var a = NewManagedPersona("TEST_ManagedReplaceA");
        var b = NewManagedPersona("TEST_ManagedReplaceB");
        var c = NewManagedPersona("TEST_ManagedReplaceC");
        await AddManagedPersonasAsync(a, b);

        await _service.ReplaceManagedPersonasAsync([b, c]);

        var stored = await _service.GetManagedPersonasAsync();
        Assert.Equal(2, stored.Count);
        // Unassignment carries no tombstone, so a merge would keep A forever.
        Assert.DoesNotContain(stored, p => p.Id == a.Id);
        Assert.Contains(stored, p => p.Id == b.Id);
        Assert.Contains(stored, p => p.Id == c.Id);
    }

    [Fact]
    public async Task ReplaceManagedPersonasAsync_Empty_ClearsStoreAndLeavesUserPersonas()
    {
        var user = await AddUserPersonaAsync("TEST_UserSurvivesManagedClear");
        await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedCleared"));

        // Present-and-empty means CLEAR. (An ABSENT channel never reaches this method at all — that decision
        // belongs to the sync apply.)
        await _service.ReplaceManagedPersonasAsync([]);

        Assert.Empty(await _service.GetManagedPersonasAsync());
        Assert.Contains(await _service.GetPersonasAsync(), p => p.Id == user.Id);
        Assert.True(UserPersonaRowExists(user.Id));
    }

    [Fact]
    public async Task ReplaceManagedPersonasAsync_RunsOffTheSharedConnection_SoAPendingTransactionCannotBreakIt()
    {
        // The sync pull calls the replace from a threadpool thread while the UI thread keeps using
        // SqliteContext's SHARED connection — including TodoService.UpdateSortOrderAsync and
        // KanbanColumnService.SetDefaultViewAsync, which hold transactions on it. One SqliteConnection cannot
        // serve a pending transaction and an untransacted command at the same time, so a replace that used
        // the shared handle would throw ("does not support nested transactions" / "Execute requires the
        // command to have a transaction object …") and abort the whole pull. Simulating the pending
        // transaction is the cheapest deterministic proxy for the cross-thread race.
        var shared = _ctx.GetConnection();
        using var pending = shared.BeginTransaction(deferred: true);
        using (var read = shared.CreateCommand())
        {
            read.Transaction = pending;
            read.CommandText = "SELECT COUNT(*) FROM ManagedPersonas";
            // Synchronous on purpose: this only has to materialize the read snapshot, and the async overload
            // would drag TestContext.Current.CancellationToken plumbing into a one-line fixture (xUnit1051).
            read.ExecuteScalar();
        }

        await _service.ReplaceManagedPersonasAsync([NewManagedPersona("TEST_ManagedDuringPendingTx")]);

        // Release the read snapshot before asserting, so the shared connection sees the committed write.
        pending.Rollback();
        Assert.Equal("TEST_ManagedDuringPendingTx", Assert.Single(await _service.GetManagedPersonasAsync()).Name);
    }

    [Fact]
    public async Task AddPersonaAsync_ManagedId_Throws()
    {
        var managed = (await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedAddGuard")))[0];

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddPersonaAsync(new Persona
        {
            Id = managed.Id,
            Name = "TEST_SmuggledManagedCopy",
            SystemPrompt = "You are a smuggled copy.",
        }));

        // Nothing landed in the pushed table.
        Assert.False(UserPersonaRowExists(managed.Id));
    }

    [Fact]
    public async Task UpdatePersonaAsync_Managed_ThrowsAndDoesNotMutateStore()
    {
        var managed = (await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedImmutable")))[0];

        var edit = (await _service.GetManagedPersonasAsync())[0];
        edit.Name = "TEST_ManagedEdited";
        edit.SystemPrompt = "Locally edited.";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdatePersonaAsync(edit));

        var after = await _service.GetManagedPersonasAsync();
        Assert.Single(after);
        Assert.Equal("TEST_ManagedImmutable", after[0].Name);
        Assert.Equal("You are an admin-published persona.", after[0].SystemPrompt);
        Assert.False(UserPersonaRowExists(managed.Id));
    }

    [Fact]
    public async Task DeletePersonaAsync_Managed_IsNoOpAndNeverTracked()
    {
        var managed = (await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedUndeletable")))[0];

        // Delete a real user persona first so the tracker file definitely exists on disk — otherwise the
        // persisted-state assertion below would pass trivially.
        var user = await AddUserPersonaAsync("TEST_UserDeletedForTracker");
        await _service.DeletePersonaAsync(user.Id);
        _created.Remove(user.Id);

        await _service.DeletePersonaAsync(managed.Id);

        var stillThere = await _service.GetManagedPersonasAsync();
        Assert.Single(stillThere);
        Assert.Equal(managed.Id, stillThere[0].Id);

        // Read the tracker back from DISK: a managed id must never become a push tombstone.
        var reloaded = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        var persisted = reloaded.GetPendingDeletes();
        Assert.True(persisted.TryGetValue("personas", out var ids));
        Assert.Contains(user.Id, ids!);
        Assert.DoesNotContain(managed.Id, ids!);
    }

    [Fact]
    public async Task ResolveActiveAsync_SelectedManagedPersona_ReturnsIt()
    {
        var managed = (await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedSelected")))[0];
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, managed.Id);

        var resolved = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);

        Assert.Equal(managed.Id, resolved.Id);
        Assert.True(resolved.IsManaged);
    }

    [Fact]
    public async Task ResolveActiveAsync_WithdrawnManagedSelection_FallsBackToBuiltIn()
    {
        var managed = (await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedVanishing")))[0];
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, managed.Id);

        await _service.ReplaceManagedPersonasAsync([]);

        var resolved = await _service.ResolveActiveAsync(WindowMode.Assistant, UserOperatingMode.Personal);
        Assert.Equal(BuiltInPersonas.PiaPersonalId, resolved.Id);
    }

    [Fact]
    public async Task ReplaceManagedPersonasAsync_WithdrawnSelection_RaisesWithdrawnOnceAndClearsSelection()
    {
        var managed = (await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedLatch")))[0];
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, managed.Id);

        var raised = new List<ManagedPersonaWithdrawnEventArgs>();
        _service.ManagedPersonaWithdrawn += (_, e) => raised.Add(e);

        await _service.ReplaceManagedPersonasAsync([]);

        Assert.Single(raised);
        Assert.Equal(managed.Id, raised[0].PersonaId);
        Assert.Equal("TEST_ManagedLatch", raised[0].PersonaName);
        Assert.Null(_settings.Settings.GetPersonaForMode(WindowMode.Assistant));

        // Clearing the selection IS the one-shot latch: the next replace can no longer see the withdrawal.
        raised.Clear();
        await _service.ReplaceManagedPersonasAsync([]);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task ReplaceManagedPersonasAsync_UserPersonaSelection_DoesNotRaiseWithdrawn()
    {
        var user = await AddUserPersonaAsync("TEST_UserSelectedNeverManaged");
        await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedUnrelated"));
        _settings.Settings.SetPersonaForMode(WindowMode.Assistant, user.Id);

        var raised = 0;
        _service.ManagedPersonaWithdrawn += (_, _) => raised++;

        await _service.ReplaceManagedPersonasAsync([]);

        Assert.Equal(0, raised);
        Assert.Equal(user.Id, _settings.Settings.GetPersonaForMode(WindowMode.Assistant));
    }

    [Fact]
    public async Task GetPersonaAsync_ManagedIdCollidingWithUserPersona_PrefersManagedAndKeepsUserRow()
    {
        var user = await AddUserPersonaAsync("TEST_UserCollision");
        await AddManagedPersonasAsync(NewManagedPersona("TEST_ManagedCollision", id: user.Id));

        var fetched = await _service.GetPersonaAsync(user.Id);
        Assert.NotNull(fetched);
        Assert.Equal("TEST_ManagedCollision", fetched!.Name);
        Assert.True(fetched.IsManaged);

        var all = await _service.GetPersonasAsync();
        Assert.Equal(1, all.Count(p => p.Id == user.Id));
        Assert.True(all.First(p => p.Id == user.Id).IsManaged);

        // The user's own row is only SUPPRESSED from the merged list, never deleted from storage.
        Assert.True(UserPersonaRowExists(user.Id));
    }

    public void Dispose()
    {
        // The whole database is this instance's own temp file, so dropping the directory is the cleanup.
        // The row-level deletes are kept only because they exercise the same paths the tests do and would
        // surface a delete that stopped working; they are no longer load-bearing for isolation.
        foreach (var id in _created)
        {
            try { _service.DeletePersonaAsync(id).GetAwaiter().GetResult(); }
            catch { /* best-effort cleanup of this instance's temp database */ }
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
