using System.Reflection;
using System.Runtime.CompilerServices;
using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services.Providers;

/// <summary>Without the table below, a NEW handler would silently default
/// <see cref="IAiProviderHandler.DropsReasoningEffortWithTools"/> to whatever its author guessed.</summary>
public class AiProviderHandlerReasoningEffortFlagTests
{
    // Keyed on the CLR Type, NOT on AiProviderType: a future handler that duplicated an existing
    // ProviderType would otherwise satisfy the count assertion below while never being checked.
    private static readonly Dictionary<Type, bool> Expected = new()
    {
        // Responses API — ToOpenAiResponses has no tool gate, so the effort survives tools.
        [typeof(OpenAiProviderHandler)] = false,
        // ToOpenAi(effort, hasTools) omits the parameter once tools are present.
        [typeof(AzureOpenAiProviderHandler)] = true,
        [typeof(OllamaProviderHandler)] = true,
        // CreateChatOptions omits the field once hasTools. TRANSPORT ONLY: for a reasoning-capable Mistral model
        // an absent field leaves reasoning ON.
        [typeof(MistralProviderHandler)] = true,
        // Reasoning injected by a DelegatingHandler, unconditionally — already boosted under tools.
        [typeof(OpenRouterProviderHandler)] = false,
        [typeof(VLlmProviderHandler)] = false,
        // Never send any reasoning field at all — a second turn could not recover anything.
        [typeof(OpenAiCompatibleProviderHandler)] = false,
        [typeof(PiaCloudProviderHandler)] = false,
    };

    [Fact]
    public void EveryHandler_DeclaresDropsReasoningEffortWithTools_WithTheExpectedValue()
    {
        var discovered = typeof(IAiProviderHandler).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAiProviderHandler).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(discovered);

        foreach (var type in discovered)
        {
            // GetUninitializedObject runs NO constructor: it inspects PiaCloudProviderHandler without a container,
            // and an initialised auto-property would read back false for every handler — a vacuous test.
            var handler = (IAiProviderHandler)RuntimeHelpers.GetUninitializedObject(type);

            Assert.True(Expected.TryGetValue(type, out var expected),
                $"{type.Name} ({handler.ProviderType}) implements IAiProviderHandler but is not listed in the "
                + "expected DropsReasoningEffortWithTools table. Decide whether its request shape drops the "
                + "configured reasoning effort when tools are attached, then add it here.");

            Assert.Equal(expected, handler.DropsReasoningEffortWithTools);
        }

        Assert.Equal(Expected.Count, discovered.Count);
    }

    [Fact]
    public void ReasoningEffortMapping_ToOpenAi_DropsEffortWithTools_ButSendsItWithout()
    {
        // The premise the `true` values rest on: on the Chat Completions path the configured effort is
        // omitted as soon as tools are attached, so a tool-using plan turn reasons at the model default.
        var withTools = ReasoningEffortMapping.ToOpenAi(ReasoningEffort.High, hasTools: true);
        var withoutTools = ReasoningEffortMapping.ToOpenAi(ReasoningEffort.High, hasTools: false);

        Assert.Null(withTools);
        Assert.NotNull(withoutTools);

        // …and the premise the OpenAI `false` value rests on: the Responses path has no tool gate.
        Assert.NotNull(ReasoningEffortMapping.ToOpenAiResponses(ReasoningEffort.High));
    }

    [Theory]
    [InlineData(typeof(AzureOpenAiProviderHandler))]
    [InlineData(typeof(OllamaProviderHandler))]
    [InlineData(typeof(MistralProviderHandler))]
    public void HandlersDeclaringTheDrop_ReallyOmitReasoningEffortUnderTools_AndSendItWithout(Type handlerType)
    {
        // Neither test above ties a handler's DECLARED transport behaviour to the request it actually builds:
        // mutate OllamaProviderHandler.CreateChatOptions to hasTools: false and both stay green.
        var handler = (IAiProviderHandler)Activator.CreateInstance(handlerType)!;
        Assert.True(handler.DropsReasoningEffortWithTools); // ties this test to the table above

        var provider = new AiProvider
        {
            Name = "X",
            Endpoint = "https://x",
            ProviderType = handler.ProviderType,
            // Only Mistral reads ModelName in CreateChatOptions (ReasoningCapableModels membership);
            // Azure and Ollama ignore it there.
            ModelName = "magistral-medium-latest",
            ReasoningEffort = ReasoningEffort.High,
        };

        Assert.False(SendsReasoningEffort(handler.CreateChatOptions(provider, hasTools: true)));
        Assert.True(SendsReasoningEffort(handler.CreateChatOptions(provider, hasTools: false)));
    }

    /// <summary>Two shapes count as omitted: no <c>RawRepresentationFactory</c> at all, and a factory whose
    /// options leave <c>ReasoningEffortLevel</c> unset.</summary>
    private static bool SendsReasoningEffort(Microsoft.Extensions.AI.ChatOptions options)
    {
        if (options.RawRepresentationFactory is null) return false;
#pragma warning disable OPENAI001
        var raw = (OpenAI.Chat.ChatCompletionOptions)options.RawRepresentationFactory(null!)!;
        return raw.ReasoningEffortLevel is not null;
#pragma warning restore OPENAI001
    }

    [Fact]
    public void MistralShouldEmitReasoning_SuppressedUnderTools_ButHighWithout()
    {
        // Pins EMISSION, not the reasoning level: an omitted field leaves a reasoning-capable Mistral model at
        // its default, which is ON — do not read `emitWithTools == false` as "this turn does not reason".
        var provider = new AiProvider
        {
            Name = "M",
            Endpoint = "https://api.mistral.ai/v1",
            ProviderType = AiProviderType.Mistral,
            ModelName = "magistral-medium-latest", // in ReasoningCapableModels
            ReasoningEffort = ReasoningEffort.High,
        };

        var (emitWithTools, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: true);
        var (emitWithoutTools, _) = MistralProviderHandler.ShouldEmitReasoning(provider, hasTools: false);

        Assert.False(emitWithTools);
        Assert.True(emitWithoutTools);
    }
}
