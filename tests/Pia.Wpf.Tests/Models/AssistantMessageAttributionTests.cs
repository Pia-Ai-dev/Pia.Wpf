using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared;
using Xunit;

namespace Pia.Tests.Models;

public class AssistantMessageAttributionTests
{
    [Fact]
    public void NoPersona_FallsBackToPiaIcon()
    {
        var msg = new AssistantMessage(ChatRole.Assistant, "hi");

        Assert.False(msg.HasPersona);
        Assert.Equal(BuiltInPersonas.PiaPersonalId, msg.PersonaGlyphId);
        Assert.Null(msg.PersonaGlyphEmoji);
    }

    [Fact]
    public void WithPersona_ExposesGlyphIdAndEmoji()
    {
        var id = Guid.NewGuid();
        var msg = new AssistantMessage(ChatRole.Assistant, "hi")
        {
            Persona = new PersonaAttribution(id, "Marketing Writer", "✍️"),
        };

        Assert.True(msg.HasPersona);
        Assert.Equal(id, msg.PersonaGlyphId);
        Assert.Equal("✍️", msg.PersonaGlyphEmoji);
    }

    [Fact]
    public void From_MapsPersonaFields()
    {
        var persona = new Persona { Name = "Coder", SystemPrompt = "x", Emoji = "💻" };

        var attr = PersonaAttribution.From(persona);

        Assert.Equal(persona.Id, attr.Id);
        Assert.Equal("Coder", attr.Name);
        Assert.Equal("💻", attr.Emoji);
    }
}
