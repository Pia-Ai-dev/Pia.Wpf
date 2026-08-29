using Pia.Models;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantHistoryMarkdownExportTests
{
    [Fact]
    public void BuildMarkdown_OpensWithTheAiMarkingFrontmatter_AndNamesTheModelUnderEachAnswer()
    {
        var chat = new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            Title = "Trip",
            UpdatedAt = new DateTime(2026, 8, 29, 8, 0, 0, DateTimeKind.Utc),
            Messages =
            [
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "Plan a trip", Timestamp = DateTime.UtcNow },
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(), Role = "assistant", Content = "Sure.", Timestamp = DateTime.UtcNow,
                    Tokens = 12, ModelName = "gpt-4o", ProviderName = "OpenAI",
                },
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(), Role = "assistant", Content = "Legacy answer.", Timestamp = DateTime.UtcNow,
                },
            ],
        };

        var md = AssistantHistoryViewModel.BuildMarkdown(chat);

        Assert.StartsWith("---", md, StringComparison.Ordinal);
        Assert.Contains($"generator: {AppVersionInfo.Generator}", md, StringComparison.Ordinal);
        Assert.Contains("aiGenerated: true", md, StringComparison.Ordinal);
        Assert.Contains("exported: ", md, StringComparison.Ordinal);
        Assert.Contains("*AI-generated · OpenAI · gpt-4o*", md, StringComparison.Ordinal);
        // A message saved before the model was recorded is still marked, just without a model.
        Assert.Contains("*AI-generated*", md, StringComparison.Ordinal);
        // User turns are never marked as machine-generated: nothing between the user heading and the next one.
        var userHeading = md.IndexOf("## User", StringComparison.Ordinal);
        var nextHeading = md.IndexOf("## Assistant", userHeading, StringComparison.Ordinal);
        Assert.True(userHeading >= 0 && nextHeading > userHeading);
        Assert.DoesNotContain("AI-generated", md[userHeading..nextHeading], StringComparison.Ordinal);
    }
}
