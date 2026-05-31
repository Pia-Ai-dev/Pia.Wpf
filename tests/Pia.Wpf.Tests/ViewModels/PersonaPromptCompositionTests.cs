using Pia.Models;
using Pia.Shared;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Unit coverage for the persona-driven prompt composition (contract §8) and tool gating (§5),
/// exercised through the pure <c>internal static</c> helpers on <see cref="AssistantViewModel"/>.
/// </summary>
public class PersonaPromptCompositionTests
{
    private static Persona Persona(string systemPrompt, string? guardrails = null, PersonaToolScope scope = PersonaToolScope.Full) => new()
    {
        Name = "Test",
        SystemPrompt = systemPrompt,
        Guardrails = guardrails,
        ToolScope = scope,
    };

    [Fact]
    public void BuildIdentityBlock_ReplacesIdentityWithPersonaPrompt()
    {
        var block = AssistantViewModel.BuildIdentityBlock(Persona("You are a senior software engineer."));

        Assert.Contains("You are a senior software engineer.", block);
        // The substrate date line is preserved below the identity.
        Assert.Contains("The current date and time is", block);
        // The old hardcoded identity is gone.
        Assert.DoesNotContain("You are Pia, a helpful personal assistant.", block);
    }

    [Fact]
    public void BuildIdentityBlock_AppendsGuardrailsWhenPresent()
    {
        var block = AssistantViewModel.BuildIdentityBlock(
            Persona("You are a financial analyst.", guardrails: "General educational information only."));

        Assert.Contains("You are a financial analyst.", block);
        Assert.Contains("General educational information only.", block);
    }

    [Fact]
    public void BuildIdentityBlock_PiaPersonal_PreservesSeededIdentity()
    {
        // Behaviour-preserving: Personal-mode users see the same identity wording after the refactor.
        var piaPersonal = BuiltInPersonas.All.First(p => Guid.Parse(p.Id) == BuiltInPersonas.PiaPersonalId);
        var block = AssistantViewModel.BuildIdentityBlock(Persona(piaPersonal.SystemPrompt));

        Assert.Contains("You are Pia, the user's warm and upbeat personal assistant.", block);
    }

    [Theory]
    [InlineData(true, PersonaToolScope.Full, true)]
    [InlineData(true, PersonaToolScope.ReadOnly, true)] // ReadOnly is treated as Full in v1.
    [InlineData(true, PersonaToolScope.None, false)]
    [InlineData(false, PersonaToolScope.Full, false)] // Provider has no tool support.
    public void ShouldUseTools_GatesOnProviderAndScope(bool providerSupportsTools, PersonaToolScope scope, bool expected)
    {
        Assert.Equal(expected, AssistantViewModel.ShouldUseTools(providerSupportsTools, scope));
    }
}
