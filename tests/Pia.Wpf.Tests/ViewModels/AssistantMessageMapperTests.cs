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
    public void RoundTrip_PreservesProviderAndSurvivesMissingTokens()
    {
        var src = new AssistantMessage(ChatRole.Assistant, "answer")
        {
            Stats = new AnswerStats(null, "gpt-4o", "OpenAI"),
        };

        var dto = AssistantMessageMapper.ToDto(src);
        Assert.Null(dto.Tokens);
        Assert.Equal("gpt-4o", dto.ModelName);
        Assert.Equal("OpenAI", dto.ProviderName);

        var back = AssistantMessageMapper.FromDto(dto);
        Assert.NotNull(back.Stats);
        Assert.Null(back.Stats!.Tokens);
        Assert.Equal("OpenAI · gpt-4o", back.Stats.ProvenanceLabel);
    }

    [Fact]
    public void RoundTrip_PreservesAttachedFiles()
    {
        var src = new AssistantMessage(ChatRole.User, "summarise these");
        src.AttachedFiles.Add(new AttachedFileRef("report.docx", "Playground/report.docx"));
        src.AttachedFiles.Add(new AttachedFileRef("scratch.txt", null));

        var dto = AssistantMessageMapper.ToDto(src);
        Assert.Equal(2, dto.AttachedFiles!.Count);
        Assert.Equal("Playground/report.docx", dto.AttachedFiles[0].RelativePath);
        Assert.Null(dto.AttachedFiles[1].RelativePath);

        var back = AssistantMessageMapper.FromDto(dto);
        Assert.True(back.HasAttachedFiles);
        Assert.Equal(["report.docx", "scratch.txt"], back.AttachedFiles.Select(f => f.FileName));
        Assert.True(back.AttachedFiles[0].IsSaved);
        Assert.False(back.AttachedFiles[1].IsSaved);
    }

    [Fact]
    public void ToDto_LeavesAttachedFilesNullWhenThereAreNone()
    {
        var dto = AssistantMessageMapper.ToDto(new AssistantMessage(ChatRole.User, "hi"));

        // Null rather than an empty list: the column stays NULL and the row keeps the size it had.
        Assert.Null(dto.AttachedFiles);
        Assert.False(AssistantMessageMapper.FromDto(dto).HasAttachedFiles);
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
