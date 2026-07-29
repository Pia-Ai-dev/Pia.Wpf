using System.Reflection;
using System.Runtime.CompilerServices;
using Pia.Models;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Wpf.Tests.Unit.Providers;

/// <summary>
/// Batch 05 conformance: <see cref="IAiProviderHandler.DropsReasoningEffortWithTools"/> is a transport
/// constant every handler must declare, and the two mappings its values rest on keep behaving the way the
/// values claim. Without the table-driven test a NEW handler would silently default to whatever its author
/// guessed, which is exactly what putting the flag on the interface is meant to prevent.
/// </summary>
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
        // ShouldEmitReasoning returns (false, default) for a non-None effort once hasTools.
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
            // GetUninitializedObject runs NO constructor. That is deliberate: it lets PiaCloudProviderHandler
            // (three ctor deps) be inspected without a container, AND it makes the test fail loudly if the
            // flag is ever written as an initialised auto-property (`{ get; } = true;`), whose initialiser
            // lives in the ctor and would read back false for every handler — a silently vacuous test.
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

    [Fact]
    public void MistralShouldEmitReasoning_SuppressedUnderTools_ButHighWithout()
    {
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
