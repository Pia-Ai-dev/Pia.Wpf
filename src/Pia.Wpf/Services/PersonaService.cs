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
/// CRUD + built-in merge for personas. Only user personas are persisted (SQLite, like
/// <see cref="TodoService"/>); the read-only built-ins from <see cref="BuiltInPersonas"/> are merged
/// in-memory and listed first (mirroring <see cref="TemplateService"/>). See
/// docs/personas/TARGET/02-pia-wpf.md §4.
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
        // Built-ins first, then user personas whose id isn't a built-in GUID.
        var merged = new List<Persona>(_builtIns);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Tagline, SystemPrompt, Guardrails, Archetype, Expertise, Emoji, AccentColor,
                   ToolScope, PreferredProviderId, ReasoningEffort, SchemaVersion, CreatedAt, UpdatedAt, OutputFormat
            FROM Personas ORDER BY CreatedAt ASC
            """;

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var persona = MapPersona(reader);
            if (!_builtInIds.Contains(persona.Id))
                merged.Add(persona);
        }

        return merged.AsReadOnly();
    }

    public async Task<Persona?> GetPersonaAsync(Guid id)
    {
        if (_builtInIds.Contains(id))
            return _builtIns.First(p => p.Id == id);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Tagline, SystemPrompt, Guardrails, Archetype, Expertise, Emoji, AccentColor,
                   ToolScope, PreferredProviderId, ReasoningEffort, SchemaVersion, CreatedAt, UpdatedAt, OutputFormat
            FROM Personas WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapPersona(reader);

        return null;
    }

    public async Task<Persona> AddPersonaAsync(Persona persona)
    {
        if (persona.Id == Guid.Empty)
            persona.Id = Guid.NewGuid();
        persona.IsBuiltIn = false;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Personas
                (Id, Name, Tagline, SystemPrompt, Guardrails, Archetype, Expertise, Emoji, AccentColor,
                 ToolScope, PreferredProviderId, ReasoningEffort, SchemaVersion, CreatedAt, UpdatedAt, OutputFormat)
            VALUES
                (@Id, @Name, @Tagline, @SystemPrompt, @Guardrails, @Archetype, @Expertise, @Emoji, @AccentColor,
                 @ToolScope, @PreferredProviderId, @ReasoningEffort, @SchemaVersion, @CreatedAt, @UpdatedAt, @OutputFormat)
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

        persona.IsBuiltIn = false;
        persona.UpdatedAt = DateTime.UtcNow;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Personas
            SET Name = @Name, Tagline = @Tagline, SystemPrompt = @SystemPrompt, Guardrails = @Guardrails,
                OutputFormat = @OutputFormat, Archetype = @Archetype, Expertise = @Expertise, Emoji = @Emoji,
                AccentColor = @AccentColor, ToolScope = @ToolScope, PreferredProviderId = @PreferredProviderId,
                ReasoningEffort = @ReasoningEffort, SchemaVersion = @SchemaVersion, UpdatedAt = @UpdatedAt
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

    private static void AddPersonaParameters(SqliteCommand command, Persona persona)
    {
        command.Parameters.AddWithValue("@Id", persona.Id.ToString());
        command.Parameters.AddWithValue("@Name", persona.Name);
        command.Parameters.AddWithValue("@Tagline", persona.Tagline is not null ? (object)persona.Tagline : DBNull.Value);
        command.Parameters.AddWithValue("@SystemPrompt", persona.SystemPrompt);
        command.Parameters.AddWithValue("@Guardrails", persona.Guardrails is not null ? (object)persona.Guardrails : DBNull.Value);
        command.Parameters.AddWithValue("@OutputFormat", persona.OutputFormat is not null ? (object)persona.OutputFormat : DBNull.Value);
        command.Parameters.AddWithValue("@Archetype", persona.Archetype is not null ? (object)persona.Archetype : DBNull.Value);
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
