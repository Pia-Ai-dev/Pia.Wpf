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
        Persona = m.Persona is { } p
            ? new SyncMessagePersona { Id = p.Id, Name = p.Name, Emoji = p.Emoji }
            : null,
    };

    public static AssistantMessage FromDto(SyncAssistantChatMessage dto)
    {
        var role = dto.Role == "user" ? ChatRole.User : ChatRole.Assistant;
        var message = new AssistantMessage(dto.Id, role, dto.Content, dto.Timestamp.ToLocalTime());
        if (!string.IsNullOrEmpty(dto.ThinkingContent))
            message.ThinkingContent = dto.ThinkingContent;
        if (dto.Tokens is { } tokens && !string.IsNullOrEmpty(dto.ModelName))
            message.Stats = new AnswerStats(tokens, dto.ModelName);
        if (dto.Persona is { } p)
            message.Persona = new PersonaAttribution(p.Id, p.Name, p.Emoji);
        return message;
    }
}
