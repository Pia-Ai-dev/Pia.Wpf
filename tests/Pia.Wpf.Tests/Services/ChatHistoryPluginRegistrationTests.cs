using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Locks the wiring that makes the chat-history pack REACHABLE by the model: a preloaded,
/// default-enabled built-in whose adapter exposes search_chats and read_chat plus a system prompt that
/// names both tools and states the two things the model cannot infer — the current chat is excluded,
/// and the history is not complete.
/// </summary>
public sealed class ChatHistoryPluginRegistrationTests
{
    private static SyncPlugin ChatHistoryConfig() =>
        BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.ChatHistoryPluginId];

    private static ChatHistoryToolHandler HandlerWith(bool enabled)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantChatHistoryToolsEnabled = enabled });
        return new ChatHistoryToolHandler(
            Substitute.For<IAssistantChatService>(), settings, NullLogger<ChatHistoryToolHandler>.Instance);
    }

    [Fact]
    public void ChatHistoryPlugin_IsPreloadedAndDefaultEnabled()
    {
        Assert.Contains(BuiltInPluginDefaults.ChatHistoryPluginId, BuiltInPluginDefaults.PreloadedPluginIds);

        var config = ChatHistoryConfig();
        Assert.True(config.IsPreloaded);
        Assert.True(config.IsActive);
        Assert.Equal("chat-history", config.Name);
        Assert.Contains("\"handlerId\":\"chat-history\"", config.ConfigJson);
        Assert.Contains("\"defaultEnabled\":true", config.ConfigJson);
    }

    [Fact]
    public void ChatHistoryPlugin_SystemPrompt_NamesBothToolsAndTheTwoLimits()
    {
        var config = ChatHistoryConfig().ConfigJson;

        // The model must learn the exact registered tool names (so it doesn't hallucinate variants)...
        Assert.Contains("search_chats", config);
        Assert.Contains("read_chat", config);

        // ...that the current chat is off limits on BOTH tools, not just search...
        Assert.Contains("never returned by search_chats", config);
        Assert.Contains("read_chat will refuse its id", config);

        // ...and that a miss means "not found", not "never happened" — retention deletes old chats.
        Assert.Contains("This history is NOT complete", config);
    }

    [Fact]
    public void FromChatHistoryHandler_ExposesBothToolsAndSystemPrompt_WhenAvailable()
    {
        var adapter = BuiltInPluginHandler.FromChatHistoryHandler(HandlerWith(enabled: true), ChatHistoryConfig());

        Assert.Contains(adapter.GetTools(), t => t.Name == "search_chats");
        Assert.Contains(adapter.GetTools(), t => t.Name == "read_chat");
        Assert.False(string.IsNullOrWhiteSpace(adapter.GetSystemPromptAddition()));
    }

    [Fact]
    public void FromChatHistoryHandler_SuppressesToolsAndPrompt_WhenUnavailable()
    {
        // The setting is the pack's only off switch: these reads are ungated on every surface.
        var adapter = BuiltInPluginHandler.FromChatHistoryHandler(HandlerWith(enabled: false), ChatHistoryConfig());

        Assert.Empty(adapter.GetTools());
        Assert.Null(adapter.GetSystemPromptAddition());
    }
}
