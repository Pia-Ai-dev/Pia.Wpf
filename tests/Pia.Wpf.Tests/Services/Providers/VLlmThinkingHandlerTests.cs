using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Models;
using Pia.Services.Providers.Http;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Providers;

public class VLlmThinkingHandlerTests
{
    [Fact]
    public void Rewrite_InjectsEnableThinkingTrue_WhenEnabled()
    {
        var body = """{"model":"Qwen/Qwen3-8B","messages":[]}""";

        var result = VLlmThinkingHandler.Rewrite(body, enableThinking: true);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        Assert.True(node["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void Rewrite_InjectsEnableThinkingFalse_WhenDisabled()
    {
        var body = """{"model":"Qwen/Qwen3-8B","messages":[]}""";

        var result = VLlmThinkingHandler.Rewrite(body, enableThinking: false);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        Assert.False(node["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void Rewrite_AlwaysStripsReasoningEffort()
    {
        var body = """{"model":"x","messages":[],"reasoning_effort":"medium"}""";

        var result = VLlmThinkingHandler.Rewrite(body, enableThinking: true);

        Assert.NotNull(result);
        var node = JsonNode.Parse(result!)!.AsObject();
        Assert.False(node.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void Rewrite_PreservesExistingChatTemplateKwargs()
    {
        var body = """{"model":"x","messages":[],"chat_template_kwargs":{"some_other_flag":true}}""";

        var result = VLlmThinkingHandler.Rewrite(body, enableThinking: true);

        Assert.NotNull(result);
        var kwargs = JsonNode.Parse(result!)!.AsObject()["chat_template_kwargs"]!.AsObject();
        Assert.True(kwargs["some_other_flag"]!.GetValue<bool>());
        Assert.True(kwargs["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SendAsync_RewritesOutgoingRequestBody()
    {
        var captured = new CapturingRequestHandler();
        var rewrite = new VLlmThinkingHandler(ReasoningEffort.Medium) { InnerHandler = captured };
        var client = new HttpClient(rewrite);

        var body = """{"model":"Qwen/Qwen3-8B","messages":[]}""";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        await client.PostAsync("http://localhost:8000/v1/chat/completions", content, TestContext.Current.CancellationToken);

        Assert.NotNull(captured.LastBody);
        var node = JsonNode.Parse(captured.LastBody!)!.AsObject();
        Assert.True(node["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

}
