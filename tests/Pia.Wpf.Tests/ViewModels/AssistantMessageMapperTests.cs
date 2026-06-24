using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantMessageMapperTests
{
    [Fact]
    public void RoundTrip_PreservesPersona()
    {
        var src = new AssistantMessage(ChatRole.Assistant, "answer")
        {
            Stats = new AnswerStats(142, "gpt-5"),
            Persona = new PersonaAttribution(Guid.NewGuid(), "Marketing Writer", "✍️"),
        };

        var dto = AssistantMessageMapper.ToDto(src);
        Assert.Equal("Marketing Writer", dto.Persona!.Name);

        var back = AssistantMessageMapper.FromDto(dto);
        Assert.True(back.HasPersona);
        Assert.Equal(src.Persona.Id, back.Persona!.Id);
        Assert.Equal("Marketing Writer", back.Persona.Name);
        Assert.Equal("✍️", back.Persona.Emoji);
    }

    [Fact]
    public void ToDto_DoesNotMapPendingConfirmation()
    {
        var src = new AssistantMessage(ChatRole.Assistant, "answer");
        src.ActionCards.Add(new ActionCardInfo
        {
            Title = "t",
            Summary = "s",
            Category = ActionCardCategory.Todo,
            ToolName = "todo",
        });
        Assert.True(src.HasPendingConfirmation);

        // The DTO has no field reflecting the in-transcript highlight; the computed
        // property cannot leak into persistence. Round-trips back to no pending card.
        var dto = AssistantMessageMapper.ToDto(src);
        var back = AssistantMessageMapper.FromDto(dto);

        Assert.Empty(back.ActionCards);
        Assert.False(back.HasPendingConfirmation);
    }

    [Fact]
    public void FromDto_LegacyMessage_NoPersona_FallsBack()
    {
        var dto = new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = "legacy",
            Timestamp = DateTime.UtcNow,
            Persona = null,
        };

        var back = AssistantMessageMapper.FromDto(dto);

        Assert.False(back.HasPersona);
        Assert.Equal(Pia.Shared.BuiltInPersonas.PiaPersonalId, back.PersonaGlyphId);
    }
}
