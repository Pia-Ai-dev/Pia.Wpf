using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.E2EE;
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

    public SyncMapper(DpapiHelper dpapiHelper, IE2EEService? e2ee = null)
    {
        _dpapiHelper = dpapiHelper;
        _e2ee = e2ee;
    }

    private bool IsE2EEActive => _e2ee?.IsReady() == true;

    private static DateTime ToUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

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
                provider.SupportsToolCalling
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
            target.ModeProviderDefaults = decrypted.ModeProviderDefaults.ToDictionary(
                kvp => (WindowMode)kvp.Key, kvp => kvp.Value);
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
        target.ModeProviderDefaults = sync.ModeProviderDefaults.ToDictionary(
            kvp => (WindowMode)kvp.Key, kvp => kvp.Value);
        target.UseSameProviderForAllModes = sync.UseSameProviderForAllModes;
    }
}
