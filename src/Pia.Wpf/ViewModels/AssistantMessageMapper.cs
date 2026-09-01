using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared.Models;

namespace Pia.ViewModels;

/// <summary>
/// Maps between the in-memory <see cref="AssistantMessage"/> and its persistence/sync DTO
/// <see cref="SyncAssistantChatMessage"/>. Single source of truth shared by AssistantViewModel
/// (save + resume) and AssistantHistoryViewModel (inspector preview).
/// </summary>
internal static class AssistantMessageMapper
{
    public static SyncAssistantChatMessage ToDto(AssistantMessage m) => new()
    {
        Id = m.Id,
        Role = m.IsUser ? "user" : "assistant",
        Content = m.Content,
        ThinkingContent = string.IsNullOrEmpty(m.ThinkingContent) ? null : m.ThinkingContent,
        Timestamp = m.Timestamp.ToUniversalTime(),
        Tokens = m.Stats?.Tokens,
        ModelName = m.Stats?.Model,
        ProviderName = m.Stats?.Provider,
        IsProtectedRoute = m.IsProtectedRoute,
        Persona = m.Persona is { } p
            ? new SyncMessagePersona { Id = p.Id, Name = p.Name, Emoji = p.Emoji }
            : null,
        AttachedFiles = m.AttachedFiles.Count == 0
            ? null
            : [.. m.AttachedFiles.Select(f => new SyncMessageAttachedFile
            {
                FileName = f.FileName,
                RelativePath = f.SavedRelativePath,
            })],
    };

    public static AssistantMessage FromDto(SyncAssistantChatMessage dto)
    {
        var role = dto.Role == "user" ? ChatRole.User : ChatRole.Assistant;
        var message = new AssistantMessage(dto.Id, role, dto.Content, dto.Timestamp.ToLocalTime());
        if (!string.IsNullOrEmpty(dto.ThinkingContent))
            message.ThinkingContent = dto.ThinkingContent;
        if (!string.IsNullOrEmpty(dto.ModelName))
            message.Stats = new AnswerStats(dto.Tokens, dto.ModelName, dto.ProviderName);
        if (dto.Persona is { } p)
            message.Persona = new PersonaAttribution(p.Id, p.Name, p.Emoji);
        message.IsProtectedRoute = dto.IsProtectedRoute;
        if (dto.AttachedFiles is { Count: > 0 } files)
            foreach (var f in files)
                message.AttachedFiles.Add(new AttachedFileRef(f.FileName, f.RelativePath));
        return message;
    }
}
