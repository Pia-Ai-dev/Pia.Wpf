using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Maps between WPF client models and shared sync DTOs.
/// When E2EE is active and userId is provided, encrypts on push and decrypts on pull.
/// </summary>
public class SyncMapper
{
    private readonly DpapiHelper _dpapiHelper;
    private readonly IE2EEService? _e2ee;
    private readonly ILogger<SyncMapper> _logger;

    public SyncMapper(DpapiHelper dpapiHelper, IE2EEService? e2ee = null, ILogger<SyncMapper>? logger = null)
    {
        _dpapiHelper = dpapiHelper;
        _e2ee = e2ee;
        _logger = logger ?? NullLogger<SyncMapper>.Instance;
    }

    private bool IsE2EEActive => _e2ee?.IsReady() == true;

    private static DateTime ToUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

    private static DateTime? ToUtc(DateTime? dt) =>
        dt.HasValue ? ToUtc(dt.Value) : null;

    // --- Templates ---

    public SyncTemplate ToSyncTemplate(OptimizationTemplate template, string? userId = null)
    {
        var sync = new SyncTemplate
        {
            Id = template.Id,
            CreatedAt = ToUtc(template.CreatedAt),
            ModifiedAt = ToUtc(template.ModifiedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                template.Name,
                template.Prompt,
                template.Description,
                template.StyleDescription
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "template", template.Id.ToString());
        }
        else
        {
            sync.Name = template.Name;
            sync.Prompt = template.Prompt;
            sync.Description = template.Description;
            sync.StyleDescription = template.StyleDescription;
        }

        return sync;
    }

    public OptimizationTemplate FromSyncTemplate(SyncTemplate sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncTemplate>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "template", sync.Id.ToString());

            return new OptimizationTemplate
            {
                Id = sync.Id,
                Name = decrypted.Name ?? "",
                Prompt = decrypted.Prompt ?? "",
                Description = decrypted.Description,
                StyleDescription = decrypted.StyleDescription,
                IsBuiltIn = false,
                CreatedAt = sync.CreatedAt,
                ModifiedAt = sync.ModifiedAt
            };
        }

        return new OptimizationTemplate
        {
            Id = sync.Id,
            Name = sync.Name ?? "",
            Prompt = sync.Prompt ?? "",
            Description = sync.Description,
            StyleDescription = sync.StyleDescription,
            IsBuiltIn = false,
            CreatedAt = sync.CreatedAt,
            ModifiedAt = sync.ModifiedAt
        };
    }

    // --- Personas ---
    //
    // E2EE field split (contract §3): the textual fields (Name, Tagline, SystemPrompt, Guardrails,
    // OutputFormat, Expertise) are encrypted into EncryptedPayload/WrappedDek with key "persona"; the
    // structural fields stay plaintext. Built-ins are never mapped to the wire (the push filter skips them),
    // and FromSyncPersona always produces a user persona (IsBuiltIn = false).

    public SyncPersona ToSyncPersona(Persona persona, string? userId = null)
    {
        var sync = new SyncPersona
        {
            Id = persona.Id,
            Archetype = persona.Archetype,
            Emoji = persona.Emoji,
            AccentColor = persona.AccentColor,
            ToolScope = (int)persona.ToolScope,
            PreferredProviderId = persona.PreferredProviderId,
            ReasoningEffort = persona.ReasoningEffort.HasValue ? (int?)persona.ReasoningEffort.Value : null,
            SchemaVersion = persona.SchemaVersion,
            CreatedAt = ToUtc(persona.CreatedAt),
            UpdatedAt = ToUtc(persona.UpdatedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                persona.Name,
                persona.Tagline,
                persona.SystemPrompt,
                persona.Guardrails,
                persona.OutputFormat,
                persona.Expertise
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "persona", persona.Id.ToString());
        }
        else
        {
            sync.Name = persona.Name;
            sync.Tagline = persona.Tagline;
            sync.SystemPrompt = persona.SystemPrompt;
            sync.Guardrails = persona.Guardrails;
            sync.OutputFormat = persona.OutputFormat;
            sync.Expertise = persona.Expertise;
        }

        return sync;
    }

    public Persona FromSyncPersona(SyncPersona sync, string? userId = null)
    {
        string? name, tagline, systemPrompt, guardrails, outputFormat;
        List<string>? expertise;

        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncPersona>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "persona", sync.Id.ToString());
            name = decrypted.Name;
            tagline = decrypted.Tagline;
            systemPrompt = decrypted.SystemPrompt;
            guardrails = decrypted.Guardrails;
            outputFormat = decrypted.OutputFormat;
            expertise = decrypted.Expertise;
        }
        else
        {
            name = sync.Name;
            tagline = sync.Tagline;
            systemPrompt = sync.SystemPrompt;
            guardrails = sync.Guardrails;
            outputFormat = sync.OutputFormat;
            expertise = sync.Expertise;
        }

        return new Persona
        {
            Id = sync.Id,
            Name = name ?? "",
            Tagline = tagline,
            SystemPrompt = systemPrompt ?? "",
            Guardrails = guardrails,
            OutputFormat = outputFormat,
            Archetype = string.IsNullOrEmpty(sync.Archetype) ? "custom" : sync.Archetype,
            Expertise = expertise ?? [],
            Emoji = sync.Emoji,
            AccentColor = sync.AccentColor,
            ToolScope = (PersonaToolScope)sync.ToolScope,
            PreferredProviderId = sync.PreferredProviderId,
            ReasoningEffort = sync.ReasoningEffort.HasValue ? (ReasoningEffort)sync.ReasoningEffort.Value : null,
            SchemaVersion = sync.SchemaVersion,
            IsBuiltIn = false,
            CreatedAt = sync.CreatedAt,
            UpdatedAt = sync.UpdatedAt
        };
    }

    // --- Providers ---

    public SyncProvider ToSyncProvider(AiProvider provider, string? userId = null)
    {
        var sync = new SyncProvider
        {
            Id = provider.Id,
            CreatedAt = ToUtc(provider.CreatedAt),
            UpdatedAt = ToUtc(provider.UpdatedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            // Decrypt API key for inclusion in encrypted payload
            var apiKey = !string.IsNullOrEmpty(provider.EncryptedApiKey)
                ? _dpapiHelper.Decrypt(provider.EncryptedApiKey) : null;

            var plainPayload = new
            {
                provider.Name,
                ProviderType = (int)provider.ProviderType,
                provider.Endpoint,
                provider.ModelName,
                ApiKey = apiKey,
                provider.AzureDeploymentName,
                provider.SupportsToolCalling,
                provider.TimeoutSeconds
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "provider", provider.Id.ToString());
        }
        else
        {
            sync.Name = provider.Name;
            sync.ProviderType = (int)provider.ProviderType;
            sync.Endpoint = provider.Endpoint;
            sync.ModelName = provider.ModelName;
            sync.ApiKey = !string.IsNullOrEmpty(provider.EncryptedApiKey)
                ? _dpapiHelper.Decrypt(provider.EncryptedApiKey) : null;
            sync.AzureDeploymentName = provider.AzureDeploymentName;
            sync.SupportsToolCalling = provider.SupportsToolCalling;
            sync.TimeoutSeconds = provider.TimeoutSeconds;
        }

        return sync;
    }

    public AiProvider FromSyncProvider(SyncProvider sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncProvider>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "provider", sync.Id.ToString());

            return new AiProvider
            {
                Id = sync.Id,
                Name = decrypted.Name ?? "",
                ProviderType = (AiProviderType)decrypted.ProviderType,
                Endpoint = decrypted.Endpoint ?? "",
                ModelName = decrypted.ModelName,
                EncryptedApiKey = !string.IsNullOrEmpty(decrypted.ApiKey)
                    ? _dpapiHelper.Encrypt(decrypted.ApiKey) : null,
                AzureDeploymentName = decrypted.AzureDeploymentName,
                SupportsToolCalling = decrypted.SupportsToolCalling,
                TimeoutSeconds = decrypted.TimeoutSeconds is > 0 ? decrypted.TimeoutSeconds : 300,
                CreatedAt = sync.CreatedAt,
                UpdatedAt = sync.UpdatedAt
            };
        }

        return new AiProvider
        {
            Id = sync.Id,
            Name = sync.Name ?? "",
            ProviderType = (AiProviderType)sync.ProviderType,
            Endpoint = sync.Endpoint ?? "",
            ModelName = sync.ModelName,
            EncryptedApiKey = !string.IsNullOrEmpty(sync.ApiKey)
                ? _dpapiHelper.Encrypt(sync.ApiKey) : null,
            AzureDeploymentName = sync.AzureDeploymentName,
            SupportsToolCalling = sync.SupportsToolCalling,
            TimeoutSeconds = sync.TimeoutSeconds is > 0 ? sync.TimeoutSeconds : 300,
            CreatedAt = sync.CreatedAt,
            UpdatedAt = sync.UpdatedAt
        };
    }

    // --- Sessions ---

    public SyncSession ToSyncSession(OptimizationSession session, string? userId = null)
    {
        var sync = new SyncSession
        {
            Id = session.Id,
            CreatedAt = ToUtc(session.CreatedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                session.OriginalText,
                session.OptimizedText,
                session.TemplateId,
                session.TemplateName,
                session.ProviderId,
                session.ProviderName,
                session.WasTranscribed,
                session.TokensUsed
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "session", session.Id.ToString());
        }
        else
        {
            sync.OriginalText = session.OriginalText;
            sync.OptimizedText = session.OptimizedText;
            sync.TemplateId = session.TemplateId;
            sync.TemplateName = session.TemplateName;
            sync.ProviderId = session.ProviderId;
            sync.ProviderName = session.ProviderName;
            sync.WasTranscribed = session.WasTranscribed;
            sync.TokensUsed = session.TokensUsed;
        }

        return sync;
    }

    public OptimizationSession FromSyncSession(SyncSession sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncSession>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "session", sync.Id.ToString());

            return new OptimizationSession
            {
                Id = sync.Id,
                OriginalText = decrypted.OriginalText ?? "",
                OptimizedText = decrypted.OptimizedText ?? "",
                TemplateId = decrypted.TemplateId,
                TemplateName = decrypted.TemplateName ?? "",
                ProviderId = decrypted.ProviderId,
                ProviderName = decrypted.ProviderName ?? "",
                WasTranscribed = decrypted.WasTranscribed,
                CreatedAt = sync.CreatedAt,
                TokensUsed = decrypted.TokensUsed
            };
        }

        return new OptimizationSession
        {
            Id = sync.Id,
            OriginalText = sync.OriginalText ?? "",
            OptimizedText = sync.OptimizedText ?? "",
            TemplateId = sync.TemplateId,
            TemplateName = sync.TemplateName ?? "",
            ProviderId = sync.ProviderId,
            ProviderName = sync.ProviderName ?? "",
            WasTranscribed = sync.WasTranscribed,
            CreatedAt = sync.CreatedAt,
            TokensUsed = sync.TokensUsed
        };
    }

    // --- Memories ---

    public SyncMemory ToSyncMemory(MemoryObject memory, string? userId = null)
    {
        var sync = new SyncMemory
        {
            Id = memory.Id,
            CreatedAt = ToUtc(memory.CreatedAt),
            UpdatedAt = ToUtc(memory.UpdatedAt),
            LastAccessedAt = ToUtc(memory.LastAccessedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                memory.Type,
                memory.Label,
                memory.Data
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "memory", memory.Id.ToString());
        }
        else
        {
            sync.Type = memory.Type;
            sync.Label = memory.Label;
            sync.Data = memory.Data;
        }

        return sync;
    }

    public MemoryObject FromSyncMemory(SyncMemory sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncMemory>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "memory", sync.Id.ToString());

            return new MemoryObject
            {
                Id = sync.Id,
                Type = decrypted.Type ?? "",
                Label = decrypted.Label ?? "",
                Data = decrypted.Data ?? "{}",
                CreatedAt = sync.CreatedAt,
                UpdatedAt = sync.UpdatedAt,
                LastAccessedAt = sync.LastAccessedAt
            };
        }

        return new MemoryObject
        {
            Id = sync.Id,
            Type = sync.Type ?? "",
            Label = sync.Label ?? "",
            Data = sync.Data ?? "{}",
            CreatedAt = sync.CreatedAt,
            UpdatedAt = sync.UpdatedAt,
            LastAccessedAt = sync.LastAccessedAt
        };
    }

    // --- Vault files (memory-vault format spec §11, contract C5) ---
    //
    // The sync unit is the FILE: each Pia-managed vault file maps to one server row keyed by its
    // frontmatter id GUID (NOT by path), so a file can be renamed/moved without orphaning its row.
    // The {path,content} envelope (VaultSyncPayload) mirrors the existing E2EE on/off split:
    //   E2EE ON  -> EncryptRecord({path,content}); Path stays null so the server never sees a
    //               plaintext path (C5); Data stays null.
    //   E2EE OFF -> Path = path, Data = content (plaintext fields round-trip the path).
    //
    // NOTE: these are NEW, standalone mappers for the vault-file sync capability (Task 5.3). The live
    // SyncClientService still syncs MemoryObject rows (To/FromSyncMemory above); that cut-over is
    // deferred (Task 4.3) and those methods are intentionally untouched.

    /// <summary>Build a <see cref="SyncMemory"/> envelope for one vault file (spec §11).</summary>
    public SyncMemory ToVaultSyncMemory(Guid id, string path, string content, string? userId = null)
    {
        var sync = new SyncMemory
        {
            Id = id
        };

        if (IsE2EEActive && userId is not null)
        {
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                new VaultSyncPayload(path, content), userId, "vault_file", id.ToString());
            // C5: leave Path/Data null — path lives only inside EncryptedPayload.
        }
        else
        {
            sync.Path = path;
            sync.Data = content;
        }

        return sync;
    }

    /// <summary>Extract the <c>(path, content)</c> of a vault file from its <see cref="SyncMemory"/> envelope.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the envelope carries ciphertext but this client cannot decrypt it (E2EE inactive,
    /// missing userId). The sync layer catches this and skips the row rather than persisting an empty
    /// vault document — mirrors <see cref="FromSyncAssistantChat"/>.
    /// </exception>
    public (string Path, string Content) FromVaultSyncMemory(SyncMemory sync, string? userId = null)
    {
        if (sync.EncryptedPayload is not null && sync.WrappedDek is not null)
        {
            if (!IsE2EEActive || userId is null)
            {
                throw new InvalidOperationException(
                    "Incoming vault file is encrypted but E2EE is not active on this client.");
            }

            var decrypted = _e2ee!.DecryptRecord<VaultSyncPayload>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "vault_file", sync.Id.ToString());
            return (decrypted.Path, decrypted.Content);
        }

        return (sync.Path ?? "", sync.Data ?? "");
    }

    // --- Todos ---

    public SyncTodo ToSyncTodo(TodoItem todo, string? userId = null)
    {
        var sync = new SyncTodo
        {
            Id = todo.Id,
            CreatedAt = ToUtc(todo.CreatedAt),
            UpdatedAt = ToUtc(todo.UpdatedAt),
            SortOrder = todo.SortOrder,
            ColumnId = todo.ColumnId
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                todo.Title,
                todo.Notes,
                Priority = (int)todo.Priority,
                Status = (int)todo.Status,
                todo.DueDate,
                todo.LinkedReminderId,
                todo.CompletedAt,
                todo.ColumnId
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "todo", todo.Id.ToString());
        }
        else
        {
            sync.Title = todo.Title;
            sync.Notes = todo.Notes;
            sync.Priority = (int)todo.Priority;
            sync.Status = (int)todo.Status;
            sync.DueDate = ToUtc(todo.DueDate);
            sync.LinkedReminderId = todo.LinkedReminderId;
            sync.CompletedAt = ToUtc(todo.CompletedAt);
        }

        return sync;
    }

    public TodoItem FromSyncTodo(SyncTodo sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncTodo>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "todo", sync.Id.ToString());

            return new TodoItem
            {
                Id = sync.Id,
                Title = decrypted.Title ?? "",
                Notes = decrypted.Notes,
                Priority = (TodoPriority)decrypted.Priority,
                Status = (TodoStatus)decrypted.Status,
                DueDate = decrypted.DueDate,
                LinkedReminderId = decrypted.LinkedReminderId,
                CreatedAt = sync.CreatedAt,
                CompletedAt = decrypted.CompletedAt,
                UpdatedAt = sync.UpdatedAt,
                SortOrder = sync.SortOrder,
                ColumnId = decrypted.ColumnId ?? sync.ColumnId
            };
        }

        return new TodoItem
        {
            Id = sync.Id,
            Title = sync.Title ?? "",
            Notes = sync.Notes,
            Priority = (TodoPriority)sync.Priority,
            Status = (TodoStatus)sync.Status,
            DueDate = sync.DueDate,
            LinkedReminderId = sync.LinkedReminderId,
            CreatedAt = sync.CreatedAt,
            CompletedAt = sync.CompletedAt,
            UpdatedAt = sync.UpdatedAt,
            SortOrder = sync.SortOrder,
            ColumnId = sync.ColumnId
        };
    }

    // --- Kanban Columns ---

    public SyncKanbanColumn ToSyncKanbanColumn(KanbanColumn column, string? userId = null)
    {
        var sync = new SyncKanbanColumn
        {
            Id = column.Id,
            SortOrder = column.SortOrder,
            IsDefaultView = column.IsDefaultView,
            IsClosedColumn = column.IsClosedColumn,
            CreatedAt = ToUtc(column.CreatedAt),
            UpdatedAt = ToUtc(column.UpdatedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                column.Name
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "kanban_column", column.Id.ToString());
        }
        else
        {
            sync.Name = column.Name;
        }

        return sync;
    }

    public KanbanColumn FromSyncKanbanColumn(SyncKanbanColumn sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncKanbanColumn>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "kanban_column", sync.Id.ToString());

            return new KanbanColumn
            {
                Id = sync.Id,
                Name = decrypted.Name ?? "",
                SortOrder = sync.SortOrder,
                IsDefaultView = sync.IsDefaultView,
                IsClosedColumn = sync.IsClosedColumn,
                CreatedAt = sync.CreatedAt,
                UpdatedAt = sync.UpdatedAt
            };
        }

        return new KanbanColumn
        {
            Id = sync.Id,
            Name = sync.Name ?? "",
            SortOrder = sync.SortOrder,
            IsDefaultView = sync.IsDefaultView,
            IsClosedColumn = sync.IsClosedColumn,
            CreatedAt = sync.CreatedAt,
            UpdatedAt = sync.UpdatedAt
        };
    }

    // --- Settings ---

    // ModeProviderDefaults push semantics: we emit exactly what's locally set.
    // Pull-side uses per-mode merge (see MergeModeProviderDefaults) — absence
    // means "no change", Guid.Empty would mean "explicit clear". We currently
    // never emit Guid.Empty, so explicit clears propagate lazily via
    // RepairModeDefaultsAsync rather than as cross-device tombstones.
    public SyncSettings ToSyncSettings(AppSettings settings, string? userId = null)
    {
        var sync = new SyncSettings
        {
            ModifiedAt = DateTime.UtcNow
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                DefaultOutputAction = (int)settings.DefaultOutputAction,
                settings.DefaultTemplateId,
                WhisperModel = (int)settings.WhisperModel,
                settings.AutoTypeDelayMs,
                Theme = (int)settings.Theme,
                settings.StartMinimized,
                TargetLanguage = settings.TargetLanguage.HasValue ? (int?)settings.TargetLanguage.Value : null,
                TargetSpeechLanguage = (int)settings.TargetSpeechLanguage,
                DefaultWindowMode = (int)settings.DefaultWindowMode,
                ModeProviderDefaults = settings.ModeProviderDefaults.ToDictionary(
                    kvp => (int)kvp.Key, kvp => kvp.Value),
                ModePersonaDefaults = settings.ModePersonaDefaults.ToDictionary(
                    kvp => (int)kvp.Key, kvp => kvp.Value),
                settings.UseSameProviderForAllModes
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "settings", "user-settings");
        }
        else
        {
            sync.DefaultOutputAction = (int)settings.DefaultOutputAction;
            sync.DefaultTemplateId = settings.DefaultTemplateId;
            sync.WhisperModel = (int)settings.WhisperModel;
            sync.AutoTypeDelayMs = settings.AutoTypeDelayMs;
            sync.Theme = (int)settings.Theme;
            sync.StartMinimized = settings.StartMinimized;
            sync.TargetLanguage = settings.TargetLanguage.HasValue ? (int)settings.TargetLanguage.Value : null;
            sync.TargetSpeechLanguage = (int)settings.TargetSpeechLanguage;
            sync.DefaultWindowMode = (int)settings.DefaultWindowMode;
            sync.ModeProviderDefaults = settings.ModeProviderDefaults.ToDictionary(
                kvp => (int)kvp.Key, kvp => kvp.Value);
            sync.ModePersonaDefaults = settings.ModePersonaDefaults.ToDictionary(
                kvp => (int)kvp.Key, kvp => kvp.Value);
            sync.UseSameProviderForAllModes = settings.UseSameProviderForAllModes;
        }

        return sync;
    }

    public void ApplySyncSettings(SyncSettings sync, AppSettings target, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncSettings>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "settings", "user-settings");

            target.DefaultOutputAction = (OutputAction)decrypted.DefaultOutputAction;
            target.DefaultTemplateId = decrypted.DefaultTemplateId;
            target.WhisperModel = (WhisperModelSize)decrypted.WhisperModel;
            target.AutoTypeDelayMs = decrypted.AutoTypeDelayMs;
            target.Theme = (AppTheme)decrypted.Theme;
            target.StartMinimized = decrypted.StartMinimized;
            target.TargetLanguage = decrypted.TargetLanguage.HasValue ? (TargetLanguage)decrypted.TargetLanguage.Value : null;
            target.TargetSpeechLanguage = (TargetSpeechLanguage)decrypted.TargetSpeechLanguage;
            target.DefaultWindowMode = (WindowMode)decrypted.DefaultWindowMode;
            MergeModeProviderDefaults(decrypted.ModeProviderDefaults, target);
            MergeModePersonaDefaults(decrypted.ModePersonaDefaults, target);
            target.UseSameProviderForAllModes = decrypted.UseSameProviderForAllModes;
            return;
        }

        target.DefaultOutputAction = (OutputAction)sync.DefaultOutputAction;
        target.DefaultTemplateId = sync.DefaultTemplateId;
        target.WhisperModel = (WhisperModelSize)sync.WhisperModel;
        target.AutoTypeDelayMs = sync.AutoTypeDelayMs;
        target.Theme = (AppTheme)sync.Theme;
        target.StartMinimized = sync.StartMinimized;
        target.TargetLanguage = sync.TargetLanguage.HasValue ? (TargetLanguage)sync.TargetLanguage.Value : null;
        target.TargetSpeechLanguage = (TargetSpeechLanguage)sync.TargetSpeechLanguage;
        target.DefaultWindowMode = (WindowMode)sync.DefaultWindowMode;
        MergeModeProviderDefaults(sync.ModeProviderDefaults, target);
        MergeModePersonaDefaults(sync.ModePersonaDefaults, target);
        target.UseSameProviderForAllModes = sync.UseSameProviderForAllModes;
    }

    // Per-mode merge with Guid.Empty tombstones.
    //
    // The previous wholesale `target.ModeProviderDefaults = incoming.ToDictionary(...)`
    // wiped the local dict whenever sync delivered an empty dictionary (e.g. from
    // an old device that never set defaults), causing the dropdown-empty regression.
    //
    // New semantics:
    // - Mode key present with Guid.Empty value  -> explicit clear (remove key locally).
    // - Mode key present with non-empty value   -> set locally.
    // - Mode key absent from incoming           -> no change locally.
    internal void MergeModeProviderDefaults(IDictionary<int, Guid> incoming, AppSettings target)
    {
        if (incoming is null) return;

        foreach (var kv in incoming)
        {
            var mode = (WindowMode)kv.Key;
            target.ModeProviderDefaults.TryGetValue(mode, out var previous);

            if (kv.Value == Guid.Empty)
            {
                if (target.ModeProviderDefaults.Remove(mode))
                    _logger.LogInformation(
                        "Sync settings: mode-default {Mode} tombstoned (was {Previous})", mode, previous);
            }
            else
            {
                target.ModeProviderDefaults[mode] = kv.Value;
                if (previous != kv.Value)
                    _logger.LogInformation(
                        "Sync settings: mode-default {Mode} {Previous} -> {New}", mode, previous, kv.Value);
            }
        }
    }

    // Per-mode merge for active-persona selection, mirroring MergeModeProviderDefaults:
    // - Mode key present with Guid.Empty value -> explicit clear (remove key locally).
    // - Mode key present with non-empty value  -> set locally.
    // - Mode key absent from incoming          -> no change locally.
    internal void MergeModePersonaDefaults(IDictionary<int, Guid> incoming, AppSettings target)
    {
        if (incoming is null) return;

        foreach (var kv in incoming)
        {
            var mode = (WindowMode)kv.Key;
            target.ModePersonaDefaults.TryGetValue(mode, out var previous);

            if (kv.Value == Guid.Empty)
            {
                if (target.ModePersonaDefaults.Remove(mode))
                    _logger.LogInformation(
                        "Sync settings: mode-persona {Mode} tombstoned (was {Previous})", mode, previous);
            }
            else
            {
                target.ModePersonaDefaults[mode] = kv.Value;
                if (previous != kv.Value)
                    _logger.LogInformation(
                        "Sync settings: mode-persona {Mode} {Previous} -> {New}", mode, previous, kv.Value);
            }
        }
    }

    // --- Scheduled Jobs ---

    public SyncScheduledJob ToSyncScheduledJob(ScheduledJob job, string? userId = null)
    {
        var sync = new SyncScheduledJob
        {
            Id = job.Id,
            OwnerDeviceId = job.OwnerDeviceId,
            CreatedAt = ToUtc(job.CreatedAt),
            UpdatedAt = ToUtc(job.UpdatedAt)
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                job.Name,
                job.Query,
                Kind = (int)job.Kind,
                GrantedTools = job.GrantedTools,
                job.ProviderId,
                Recurrence = (int)job.Recurrence,
                job.TimeOfDay,
                DayOfWeek = job.DayOfWeek.HasValue ? (int?)(int)job.DayOfWeek.Value : null,
                job.DayOfMonth,
                job.Month,
                SpecificDate = ToUtc(job.SpecificDate),
                Status = (int)job.Status
            };
            (sync.EncryptedPayload, sync.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "scheduled_job", job.Id.ToString());
        }
        else
        {
            sync.Name = job.Name;
            sync.Query = job.Query;
            sync.Kind = (int)job.Kind;
            sync.GrantedTools = job.GrantedTools;
            sync.ProviderId = job.ProviderId;
            sync.Recurrence = (int)job.Recurrence;
            sync.TimeOfDay = job.TimeOfDay;
            sync.DayOfWeek = job.DayOfWeek.HasValue ? (int?)(int)job.DayOfWeek.Value : null;
            sync.DayOfMonth = job.DayOfMonth;
            sync.Month = job.Month;
            sync.SpecificDate = ToUtc(job.SpecificDate);
            sync.Status = (int)job.Status;
        }

        return sync;
    }

    public ScheduledJob FromSyncScheduledJob(SyncScheduledJob sync, string? userId = null)
    {
        if (IsE2EEActive
            && sync.EncryptedPayload is not null
            && sync.WrappedDek is not null
            && userId is not null)
        {
            var decrypted = _e2ee!.DecryptRecord<SyncScheduledJob>(
                sync.EncryptedPayload, sync.WrappedDek, userId, "scheduled_job", sync.Id.ToString());

            return new ScheduledJob
            {
                Id = sync.Id,
                Name = decrypted.Name ?? "",
                Query = decrypted.Query ?? "",
                Kind = (ScheduledJobKind)(decrypted.Kind ?? 0),
                GrantedTools = decrypted.GrantedTools ?? [],
                ProviderId = decrypted.ProviderId,
                Recurrence = (RecurrenceType)(decrypted.Recurrence ?? 0),
                TimeOfDay = decrypted.TimeOfDay ?? default,
                DayOfWeek = decrypted.DayOfWeek.HasValue ? (DayOfWeek)decrypted.DayOfWeek.Value : null,
                DayOfMonth = decrypted.DayOfMonth,
                Month = decrypted.Month,
                SpecificDate = decrypted.SpecificDate,
                Status = (ScheduledJobStatus)(decrypted.Status ?? 0),
                CreatedAt = sync.CreatedAt,
                UpdatedAt = sync.UpdatedAt,
                OwnerDeviceId = sync.OwnerDeviceId
            };
        }

        return new ScheduledJob
        {
            Id = sync.Id,
            Name = sync.Name ?? "",
            Query = sync.Query ?? "",
            Kind = (ScheduledJobKind)(sync.Kind ?? 0),
            GrantedTools = sync.GrantedTools ?? [],
            ProviderId = sync.ProviderId,
            Recurrence = (RecurrenceType)(sync.Recurrence ?? 0),
            TimeOfDay = sync.TimeOfDay ?? default,
            DayOfWeek = sync.DayOfWeek.HasValue ? (DayOfWeek)sync.DayOfWeek.Value : null,
            DayOfMonth = sync.DayOfMonth,
            Month = sync.Month,
            SpecificDate = sync.SpecificDate,
            Status = (ScheduledJobStatus)(sync.Status ?? 0),
            CreatedAt = sync.CreatedAt,
            UpdatedAt = sync.UpdatedAt,
            OwnerDeviceId = sync.OwnerDeviceId
        };
    }

    // --- Research Sessions ---
    // Removed with the research view: research results are now persisted as assistant chats
    // (see IBackgroundAssistantTurnRunner). The SyncResearchSession DTO + push/pull fields are
    // retained for server wire-contract stability but are no longer produced/consumed by the client.

    // --- Assistant Chats ---

    /// <summary>
    /// Prepare a chat for the wire. When E2EE is active, encrypts Title / ProviderId / Messages
    /// into EncryptedPayload+WrappedDek and clears the plaintext fields. Otherwise returns a
    /// copy unchanged. Id, SchemaVersion, timestamps, and WindowMode stay plaintext — the
    /// server needs them for indexing, conflict resolution, and validation.
    /// </summary>
    public SyncAssistantChat ToSyncAssistantChat(SyncAssistantChat chat, string? userId = null)
    {
        var wire = new SyncAssistantChat
        {
            Id = chat.Id,
            SchemaVersion = chat.SchemaVersion,
            CreatedAt = ToUtc(chat.CreatedAt),
            UpdatedAt = ToUtc(chat.UpdatedAt),
            LastAccessedAt = ToUtc(chat.LastAccessedAt),
            WindowMode = chat.WindowMode,
            ExtensionData = chat.ExtensionData
        };

        if (IsE2EEActive && userId is not null)
        {
            var plainPayload = new
            {
                chat.Title,
                chat.ProviderId,
                chat.Messages
            };
            (wire.EncryptedPayload, wire.WrappedDek) = _e2ee!.EncryptRecord(
                plainPayload, userId, "assistant_chat", chat.Id.ToString());
            // Leave Title/ProviderId/Messages defaults — server enforces they stay empty
            // when EncryptedPayload is set (see assistant-chat-history.md §4.3).
        }
        else
        {
            wire.Title = chat.Title;
            wire.ProviderId = chat.ProviderId;
            wire.Messages = chat.Messages;
        }

        return wire;
    }

    /// <summary>
    /// Materialize an incoming wire chat into a local-store-ready document. When E2EE
    /// is active and the wire carries ciphertext, decrypts Title / ProviderId / Messages
    /// back onto the document and clears EncryptedPayload/WrappedDek so the local store
    /// stays plaintext (matches the pattern used for templates/memories/etc.).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the wire chat carries ciphertext but this client cannot decrypt it
    /// (E2EE inactive, missing userId). The sync service catches this and skips the row
    /// rather than persisting an empty plaintext chat to local storage.
    /// </exception>
    public SyncAssistantChat FromSyncAssistantChat(SyncAssistantChat wire, string? userId = null)
    {
        if (wire.EncryptedPayload is not null && wire.WrappedDek is not null)
        {
            if (!IsE2EEActive || userId is null)
            {
                throw new InvalidOperationException(
                    "Incoming chat is encrypted but E2EE is not active on this client.");
            }

            var decrypted = _e2ee!.DecryptRecord<SyncAssistantChat>(
                wire.EncryptedPayload, wire.WrappedDek, userId, "assistant_chat", wire.Id.ToString());

            return new SyncAssistantChat
            {
                Id = wire.Id,
                SchemaVersion = wire.SchemaVersion,
                Title = decrypted.Title,
                ProviderId = decrypted.ProviderId,
                Messages = decrypted.Messages ?? [],
                CreatedAt = wire.CreatedAt,
                UpdatedAt = wire.UpdatedAt,
                LastAccessedAt = wire.LastAccessedAt,
                WindowMode = wire.WindowMode,
                ExtensionData = wire.ExtensionData
                // EncryptedPayload / WrappedDek deliberately null — local store holds plaintext.
            };
        }

        return new SyncAssistantChat
        {
            Id = wire.Id,
            SchemaVersion = wire.SchemaVersion,
            Title = wire.Title,
            ProviderId = wire.ProviderId,
            Messages = wire.Messages,
            CreatedAt = wire.CreatedAt,
            UpdatedAt = wire.UpdatedAt,
            LastAccessedAt = wire.LastAccessedAt,
            WindowMode = wire.WindowMode,
            ExtensionData = wire.ExtensionData
        };
    }
}
