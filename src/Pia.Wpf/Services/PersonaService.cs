using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared;

namespace Pia.Services;

/// <summary>
/// CRUD + built-in merge for personas. User personas are persisted in <c>Personas</c> (SQLite, like
/// <see cref="TodoService"/>); the read-only built-ins from <see cref="BuiltInPersonas"/> are merged
/// in-memory and listed first (mirroring <see cref="TemplateService"/>). See
/// docs/personas/TARGET/02-pia-wpf.md §4.
/// <para>
/// Admin-published managed personas live in a SEPARATE <c>ManagedPersonas</c> table, written only by
/// <see cref="ReplaceManagedPersonasAsync"/> from the sync pull's replace-all snapshot. The separation is
/// structural, not cosmetic: <c>Personas</c> is the push source, so keeping managed rows out of it makes
/// "never push a managed persona" impossible to forget rather than a filter someone has to remember.
/// </para>
/// </summary>
public class PersonaService : IPersonaService
{
    private readonly SqliteContext _context;
    private readonly ILogger<PersonaService> _logger;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly ISettingsService _settingsService;

    private readonly IReadOnlyList<Persona> _builtIns;
    private readonly HashSet<Guid> _builtInIds;

    public event EventHandler? PersonasChanged;
    public event EventHandler<ManagedPersonaWithdrawnEventArgs>? ManagedPersonaWithdrawn;

    public PersonaService(
        SqliteContext context,
        ILogger<PersonaService> logger,
        SyncDeleteTrackerService deleteTracker,
        ISettingsService settingsService)
    {
        _context = context;
        _logger = logger;
        _deleteTracker = deleteTracker;
        _settingsService = settingsService;

        _builtIns = CreateBuiltInPersonas();
        _builtInIds = _builtIns.Select(p => p.Id).ToHashSet();
    }

    private void OnPersonasChanged() => PersonasChanged?.Invoke(this, EventArgs.Empty);

    public async Task<IReadOnlyList<Persona>> GetPersonasAsync()
    {
        // Built-ins, then managed, then user personas:
        //  - built-ins first preserves the guarantee that ResolveActiveAsync's fallback First(...) never
        //    throws, and keeps the picker's familiar head;
        //  - managed before user personas because they are org-level and should not be buried under a long
        //    personal list;
        //  - both trailing blocks stay internally ordered by CreatedAt ASC, matching today's user ordering.
        var merged = new List<Persona>(_builtIns);

        // Id-collision precedence is built-in > managed > user. Guard 1: a managed id equal to a BUILT-IN id
        // is dropped from the managed block. The server always mints a fresh GUID so this should be
        // unreachable, but it costs one HashSet.Contains.
        var managed = (await GetManagedPersonasAsync())
            .Where(p => !_builtInIds.Contains(p.Id))
            .ToList();
        merged.AddRange(managed);
        var managedIds = managed.Select(p => p.Id).ToHashSet();

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {PersonaColumns}
            FROM Personas ORDER BY CreatedAt ASC
            """;

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var persona = MapPersona(reader);
            // Guard 2: a user row whose id collides with a managed row is SUPPRESSED from this list — the
            // managed row wins — but is deliberately NOT deleted from SQLite. It is the user's own data and
            // syncs on the push channel; suppression here is a display-precedence rule, not a deletion.
            // (Only reachable if the user hand-crafted a persona with a colliding GUID.)
            if (!_builtInIds.Contains(persona.Id) && !managedIds.Contains(persona.Id))
                merged.Add(persona);
        }

        return merged.AsReadOnly();
    }

    public async Task<Persona?> GetPersonaAsync(Guid id)
    {
        if (_builtInIds.Contains(id))
            return _builtIns.First(p => p.Id == id);

        // Managed before user, so a colliding id resolves to the same row here as in GetPersonasAsync.
        var managed = await GetManagedPersonaAsync(id);
        if (managed is not null)
            return managed;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {PersonaColumns}
            FROM Personas WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapPersona(reader);

        return null;
    }

    public Task<IReadOnlyList<Persona>> GetManagedPersonasAsync() =>
        ReadManagedPersonasAsync(_context.GetConnection());

    /// <summary>
    /// Reads the whole managed store off <paramref name="connection"/>. Parameterized on the connection
    /// because <see cref="ReplaceManagedPersonasAsync"/> runs entirely on its own handle (see there) while
    /// every other caller uses the shared one.
    /// </summary>
    private static async Task<IReadOnlyList<Persona>> ReadManagedPersonasAsync(SqliteConnection connection)
    {
        var managed = new List<Persona>();

        using var command = connection.CreateCommand();
        // Literally the same column list as the Personas query above (PersonaColumns) and the same ORDER BY.
        // ManagedPersonas is deliberately column-identical to Personas, and that identity is the entire
        // reason MapPersona is reusable here (it hardcodes ordinals — OutputFormat at 15).
        command.CommandText = $"""
            SELECT {PersonaColumns}
            FROM ManagedPersonas ORDER BY CreatedAt ASC
            """;

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var persona = MapPersona(reader);
            // MapPersona cannot know which table it read from. Its IsBuiltIn = false is correct for managed
            // rows too, so IsManaged is the only flag the caller has to stamp.
            persona.IsManaged = true;
            managed.Add(persona);
        }

        return managed.AsReadOnly();
    }

    public async Task ReplaceManagedPersonasAsync(IReadOnlyList<Persona> personas)
    {
        // Force the shared context open first: it owns EnsureSchema (so ManagedPersonas exists) and sets
        // journal_mode=WAL, which is a persistent PER-FILE setting and therefore also covers the dedicated
        // connection below. Idempotent — the app opens it during composition anyway.
        _context.GetConnection();

        // A DEDICATED connection for the WHOLE replace, not _context.GetConnection(). This is the app's only
        // background-thread transaction (the sync pull calls this from a threadpool thread), and ONE
        // SqliteConnection cannot hold a pending transaction while another caller issues an untransacted
        // command on it: Microsoft.Data.Sqlite throws "Execute requires the command to have a transaction
        // object when the connection assigned to the command is in a pending local transaction". A UI-thread
        // read from any of the ten services on the shared connection (TodoService, MemoryService, …) landing
        // inside this DELETE-all + N-INSERT would take that throw — the exact hazard AssistantChatService's
        // header documents, and why the chat store moved off the shared handle. In WAL the reverse direction
        // is safe too: a pending UI-thread transaction on the shared connection cannot make this replace
        // throw. Same idiom as AgentRunService / FlowPersistenceStore.
        using var connection = new SqliteConnection(_context.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            // busy_timeout is PER-CONNECTION: without it, a concurrent write on the shared handle turns a
            // short wait into an immediate "database is locked".
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }

        // Snapshot the CURRENT managed rows BEFORE the delete: the old id → name map is the only way to tell
        // a genuine withdrawal (a selected id that WAS managed and is now gone) from a selection pointing at
        // a user persona or a built-in, which must never raise the notification. Read on this connection, so
        // the whole operation is independent of what the shared handle is doing.
        var previous = await ReadManagedPersonasAsync(connection);
        var previousNames = previous.ToDictionary(p => p.Id, p => p.Name);
        var incomingIds = personas.Select(p => p.Id).ToHashSet();

        using var transaction = connection.BeginTransaction();

        try
        {
            // Replace-all, never merge: unassignment carries no tombstone, so a merge would keep revoked
            // personas forever. The transaction is what stops a mid-insert failure from leaving the store
            // half-empty (which would look exactly like a revocation to the code above).
            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM ManagedPersonas";
                await deleteCommand.ExecuteNonQueryAsync();
            }

            foreach (var persona in personas)
            {
                // Pin both flags on the way in: managed rows are neither built-in nor user-owned, and the
                // store must never hold a row that claims otherwise.
                persona.IsBuiltIn = false;
                persona.IsManaged = true;

                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT OR REPLACE INTO ManagedPersonas
                        (Id, Name, Tagline, SystemPrompt, Guardrails, Archetype, Expertise, Emoji, AccentColor,
                         ToolScope, PreferredProviderId, ReasoningEffort, SchemaVersion, CreatedAt, UpdatedAt, OutputFormat, ModelType)
                    VALUES
                        (@Id, @Name, @Tagline, @SystemPrompt, @Guardrails, @Archetype, @Expertise, @Emoji, @AccentColor,
                         @ToolScope, @PreferredProviderId, @ReasoningEffort, @SchemaVersion, @CreatedAt, @UpdatedAt, @OutputFormat, @ModelType)
                    """;
                AddPersonaParameters(insertCommand, persona);
                await insertCommand.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        _logger.LogInformation(
            "Replaced managed personas: {Count} (previously {PreviousCount})", personas.Count, previous.Count);
        _logger.SensitiveDebug(
            "Managed persona snapshot names: {Names}", string.Join(", ", personas.Select(p => p.Name)));

        // A selection that pointed at a now-withdrawn managed persona is cleared here, in the same operation
        // as the replace (after the commit, so it only ever follows a store that really changed).
        // Clearing it IS the one-shot latch: the next replace can no longer see the old id in
        // ModePersonaDefaults, so the same withdrawal cannot be detected twice and no persisted flag is
        // needed. ResolveActiveAsync then falls back to the operating-mode built-in on its own.
        var settings = await _settingsService.GetSettingsAsync();
        var withdrawnIds = new List<Guid>();

        // Enumerate a snapshot: SetPersonaForMode removes the key, which would invalidate a live enumerator.
        foreach (var (mode, selectedId) in settings.ModePersonaDefaults.ToList())
        {
            if (previousNames.ContainsKey(selectedId) && !incomingIds.Contains(selectedId))
            {
                settings.SetPersonaForMode(mode, null);
                withdrawnIds.Add(selectedId);
            }
        }

        // The AGENT roster needs the same walk, and it is deliberately NOT folded into withdrawnIds: that list
        // drives the ManagedPersonaWithdrawn notification, whose message is "…is no longer available; switched
        // to X" — true of a per-mode CHAT selection, which falls back to a replacement persona, and false of a
        // roster line, which simply stops being offered to the planner. So a roster-only withdrawal saves
        // silently. Same withdrawal test as above (was managed, is now gone), so a roster id pointing at a user
        // persona or a built-in is left alone, and the removal is one-shot for the same reason the selection
        // clear is: the next replace can no longer find the old id here.
        var rosterChanged = false;

        // Snapshot again: SetAgentPersonaRoster removes the key when nothing is left.
        foreach (var (mode, ids) in settings.AgentPersonaRoster.ToList())
        {
            var kept = ids.Where(id => !previousNames.ContainsKey(id) || incomingIds.Contains(id)).ToList();
            if (kept.Count == ids.Count)
                continue;

            // Only written when this mode really lost a line, so the read-side clamp in
            // SetAgentPersonaRoster cannot silently truncate a roster this replace had no business touching.
            settings.SetAgentPersonaRoster(mode, kept);
            rosterChanged = true;
        }

        if (withdrawnIds.Count > 0 || rosterChanged)
        {
            await _settingsService.SaveSettingsAsync(settings);

            // One event per DISTINCT withdrawn persona (two modes can select the same one), ordered by id so
            // the snackbar sequence does not depend on dictionary enumeration order.
            foreach (var id in withdrawnIds.Distinct().OrderBy(id => id))
            {
                _logger.LogInformation(
                    "Managed persona {Id} withdrawn; cleared the dangling per-mode selection", id);
                _logger.SensitiveDebug("Withdrawn managed persona {Id} name: {Name}", id, previousNames[id]);
                ManagedPersonaWithdrawn?.Invoke(this, new ManagedPersonaWithdrawnEventArgs
                {
                    PersonaId = id,
                    PersonaName = previousNames[id]
                });
            }
        }

        OnPersonasChanged();
    }

    public async Task<Persona> AddPersonaAsync(Persona persona)
    {
        if (persona.Id == Guid.Empty)
            persona.Id = Guid.NewGuid();
        else if (await IsManagedIdAsync(persona.Id))
            throw new InvalidOperationException(
                "Cannot add a persona under a managed persona's id: managed personas are admin-published");

        persona.IsBuiltIn = false;
        // Personas is the PUSH source (SyncClientService reads it to build personas.upserted), so also pin
        // IsManaged = false: a caller must not be able to smuggle a managed row into the pushed table.
        persona.IsManaged = false;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Personas
                (Id, Name, Tagline, SystemPrompt, Guardrails, Archetype, Expertise, Emoji, AccentColor,
                 ToolScope, PreferredProviderId, ReasoningEffort, SchemaVersion, CreatedAt, UpdatedAt, OutputFormat, ModelType)
            VALUES
                (@Id, @Name, @Tagline, @SystemPrompt, @Guardrails, @Archetype, @Expertise, @Emoji, @AccentColor,
                 @ToolScope, @PreferredProviderId, @ReasoningEffort, @SchemaVersion, @CreatedAt, @UpdatedAt, @OutputFormat, @ModelType)
            """;

        AddPersonaParameters(command, persona);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Added persona {Id} (ToolScope: {ToolScope})", persona.Id, persona.ToolScope);
        _logger.SensitiveDebug("Added persona {Id} name: {Name}", persona.Id, persona.Name);
        OnPersonasChanged();
        return persona;
    }

    public async Task UpdatePersonaAsync(Persona persona)
    {
        if (_builtInIds.Contains(persona.Id))
            throw new InvalidOperationException("Cannot modify built-in personas");

        // Same guard, same reason as built-ins: managed personas are admin-authored and the whole store is
        // replaced by every pull, so a local edit would be silently overwritten even if it were allowed.
        if (await IsManagedIdAsync(persona.Id))
            throw new InvalidOperationException(
                "Cannot modify managed personas: they are admin-published and replaced by the sync pull");

        persona.IsBuiltIn = false;
        persona.UpdatedAt = DateTime.UtcNow;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Personas
            SET Name = @Name, Tagline = @Tagline, SystemPrompt = @SystemPrompt, Guardrails = @Guardrails,
                OutputFormat = @OutputFormat, Archetype = @Archetype, Expertise = @Expertise, Emoji = @Emoji,
                AccentColor = @AccentColor, ToolScope = @ToolScope, PreferredProviderId = @PreferredProviderId,
                ReasoningEffort = @ReasoningEffort, SchemaVersion = @SchemaVersion, UpdatedAt = @UpdatedAt,
                ModelType = @ModelType
            WHERE Id = @Id
            """;

        AddPersonaParameters(command, persona);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Updated persona {Id}", persona.Id);
        _logger.SensitiveDebug("Updated persona {Id} name: {Name}", persona.Id, persona.Name);
        OnPersonasChanged();
    }

    public async Task DeletePersonaAsync(Guid id)
    {
        if (_builtInIds.Contains(id))
        {
            _logger.LogDebug("Skipped delete for persona {Id}: built-in personas cannot be deleted", id);
            return;
        }

        // This return MUST stay above the _deleteTracker.TrackDeletion call below — that is the whole point
        // of the guard. The DELETE targets Personas, so a managed id would not remove the managed row
        // anyway; the hazard is purely the tracker, which would enqueue a push tombstone for a row this
        // client does not own. The server quarantines such a tombstone, but the client contract is to never
        // emit one. Do not "simplify" this check down past the tracker.
        if (await IsManagedIdAsync(id))
        {
            _logger.LogDebug("Skipped delete for persona {Id}: managed personas cannot be deleted locally", id);
            return;
        }

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Personas WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync();
        _deleteTracker.TrackDeletion("personas", id);
        _logger.LogInformation("Deleted persona {Id}", id);
        OnPersonasChanged();
    }

    public async Task<Persona> ResolveActiveAsync(WindowMode mode, UserOperatingMode operatingMode)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var personas = await GetPersonasAsync();

        var selectedId = settings.GetPersonaForMode(mode);
        if (selectedId.HasValue)
        {
            var match = personas.FirstOrDefault(p => p.Id == selectedId.Value);
            if (match is not null)
                return match;

            _logger.LogInformation(
                "Active persona {Id} for mode {Mode} not found; falling back to operating-mode default",
                selectedId.Value, mode);
        }

        var fallbackId = operatingMode == UserOperatingMode.Business
            ? BuiltInPersonas.PiaBusinessId
            : BuiltInPersonas.PiaPersonalId;

        // Built-ins are always present, so First never throws here.
        return personas.First(p => p.Id == fallbackId);
    }

    /// <summary>Single-row managed read, mirroring the <c>Personas</c> lookup in <see cref="GetPersonaAsync"/>.</summary>
    private async Task<Persona?> GetManagedPersonaAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {PersonaColumns}
            FROM ManagedPersonas WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var persona = MapPersona(reader);
            persona.IsManaged = true;
            return persona;
        }

        return null;
    }

    /// <summary>
    /// Existence probe for the write-path guards. Managed ownership is a fact about the store, not about the
    /// caller's <see cref="Persona.IsManaged"/> flag, so every write guard asks the table — a caller cannot
    /// clear a flag to get past it.
    /// </summary>
    private async Task<bool> IsManagedIdAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM ManagedPersonas WHERE Id = @Id LIMIT 1";
        command.Parameters.AddWithValue("@Id", id.ToString());

        return await command.ExecuteScalarAsync() is not null;
    }

    private static void AddPersonaParameters(SqliteCommand command, Persona persona)
    {
        command.Parameters.AddWithValue("@Id", persona.Id.ToString());
        command.Parameters.AddWithValue("@Name", persona.Name);
        command.Parameters.AddWithValue("@Tagline", persona.Tagline is not null ? (object)persona.Tagline : DBNull.Value);
        command.Parameters.AddWithValue("@SystemPrompt", persona.SystemPrompt);
        command.Parameters.AddWithValue("@Guardrails", persona.Guardrails is not null ? (object)persona.Guardrails : DBNull.Value);
        command.Parameters.AddWithValue("@OutputFormat", persona.OutputFormat is not null ? (object)persona.OutputFormat : DBNull.Value);
        command.Parameters.AddWithValue("@Archetype", persona.Archetype is not null ? (object)persona.Archetype : DBNull.Value);
        command.Parameters.AddWithValue("@ModelType", persona.ModelType is not null ? (object)persona.ModelType : DBNull.Value);
        command.Parameters.AddWithValue("@Expertise", JsonSerializer.Serialize(persona.Expertise ?? []));
        command.Parameters.AddWithValue("@Emoji", persona.Emoji is not null ? (object)persona.Emoji : DBNull.Value);
        command.Parameters.AddWithValue("@AccentColor", persona.AccentColor is not null ? (object)persona.AccentColor : DBNull.Value);
        command.Parameters.AddWithValue("@ToolScope", (int)persona.ToolScope);
        command.Parameters.AddWithValue("@PreferredProviderId", persona.PreferredProviderId.HasValue ? (object)persona.PreferredProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@ReasoningEffort", persona.ReasoningEffort.HasValue ? (object)(int)persona.ReasoningEffort.Value : DBNull.Value);
        command.Parameters.AddWithValue("@SchemaVersion", persona.SchemaVersion);
        command.Parameters.AddWithValue("@CreatedAt", persona.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", persona.UpdatedAt.ToString("O"));
    }

    /// <summary>
    /// The projection <see cref="MapPersona"/> reads BY ORDINAL (Id 0 … UpdatedAt 14, OutputFormat 15,
    /// ModelType 16), shared by all four persona SELECTs. One constant rather than four copies because
    /// <c>Personas</c> and <c>ManagedPersonas</c> are column-identical and that identity is the whole reason
    /// one reader serves both: a 17th column added to only three of the four SELECTs would make
    /// <see cref="MapPersona"/> throw <see cref="IndexOutOfRangeException"/> on every read from the table
    /// that was missed. Adding a column means editing this list and <see cref="MapPersona"/> together.
    /// </summary>
    private const string PersonaColumns = """
        Id, Name, Tagline, SystemPrompt, Guardrails, Archetype, Expertise, Emoji, AccentColor,
        ToolScope, PreferredProviderId, ReasoningEffort, SchemaVersion, CreatedAt, UpdatedAt, OutputFormat, ModelType
        """;

    private static Persona MapPersona(SqliteDataReader reader)
    {
        var expertiseJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new Persona
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Tagline = reader.IsDBNull(2) ? null : reader.GetString(2),
            SystemPrompt = reader.GetString(3),
            Guardrails = reader.IsDBNull(4) ? null : reader.GetString(4),
            // OutputFormat is appended last in the SELECT (index 15) to keep the other ordinals stable.
            OutputFormat = reader.IsDBNull(15) ? null : reader.GetString(15),
            Archetype = reader.IsDBNull(5) ? "custom" : reader.GetString(5),
            ModelType = NormalizeModelType(reader.IsDBNull(16) ? null : reader.GetString(16)),
            Expertise = ParseExpertise(expertiseJson),
            Emoji = reader.IsDBNull(7) ? null : reader.GetString(7),
            AccentColor = reader.IsDBNull(8) ? null : reader.GetString(8),
            ToolScope = (PersonaToolScope)reader.GetInt32(9),
            PreferredProviderId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            ReasoningEffort = reader.IsDBNull(11) ? null : (ReasoningEffort)reader.GetInt32(11),
            SchemaVersion = reader.GetInt32(12),
            CreatedAt = DateTime.Parse(reader.GetString(13)),
            UpdatedAt = DateTime.Parse(reader.GetString(14)),
            IsBuiltIn = false
        };
    }

    // Blank is never a stored meaning: every persona routes with at least the default type.
    private static string NormalizeModelType(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Persona.DefaultModelType : value;

    private static List<string> ParseExpertise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private List<Persona> CreateBuiltInPersonas()
    {
        return BuiltInPersonas.All.Select(p => new Persona
        {
            Id = new Guid(p.Id),
            Name = p.Name,
            Tagline = p.Tagline,
            SystemPrompt = p.SystemPrompt,
            Guardrails = p.Guardrails,
            OutputFormat = p.OutputFormat,
            Archetype = p.Archetype,
            ModelType = Persona.DefaultModelType,
            Expertise = [.. p.Expertise],
            Emoji = p.Emoji,
            AccentColor = p.AccentColor,
            ToolScope = (PersonaToolScope)p.ToolScope,
            PreferredProviderId = null,
            ReasoningEffort = null,
            SchemaVersion = 1,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ToList();
    }
}
