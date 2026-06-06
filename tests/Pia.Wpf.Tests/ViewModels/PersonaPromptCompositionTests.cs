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
    private static Persona Persona(string systemPrompt, string? guardrails = null, PersonaToolScope scope = PersonaToolScope.Full, string? outputFormat = null) => new()
    {
        Name = "Test",
        SystemPrompt = systemPrompt,
        Guardrails = guardrails,
        OutputFormat = outputFormat,
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

    [Fact]
    public void ResolveOutputFormat_UsesPersonaValue_WhenSet()
    {
        var custom = "- Always answer in haiku.\n- Never use code blocks.";
        var resolved = AssistantViewModel.ResolveOutputFormat(Persona("You are a poet.", outputFormat: custom));

        Assert.Equal(custom, resolved);
        // The substrate default is NOT used when the persona defines its own format.
        Assert.DoesNotContain("Keep replies short", resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveOutputFormat_FallsBackToDefault_WhenBlank(string? outputFormat)
    {
        var resolved = AssistantViewModel.ResolveOutputFormat(Persona("You are helpful.", outputFormat: outputFormat));

        Assert.Equal(AssistantViewModel.DefaultOutputFormat, resolved);
    }

    [Fact]
    public void ResolveOutputFormat_TrimsPersonaValue()
    {
        var resolved = AssistantViewModel.ResolveOutputFormat(
            Persona("You are helpful.", outputFormat: "\n  - Be brief.  \n"));

        Assert.Equal("- Be brief.", resolved);
    }

    [Fact]
    public void DefaultOutputFormat_MatchesPiaBuiltInsOutputFormat()
    {
        // Pia personas must render the historical formatting block; the catalog value and the
        // substrate fallback are kept byte-identical so either path produces the same text.
        var piaPersonal = BuiltInPersonas.All.First(p => Guid.Parse(p.Id) == BuiltInPersonas.PiaPersonalId);
        var piaBusiness = BuiltInPersonas.All.First(p => Guid.Parse(p.Id) == BuiltInPersonas.PiaBusinessId);

        Assert.Equal(AssistantViewModel.DefaultOutputFormat, piaPersonal.OutputFormat);
        Assert.Equal(AssistantViewModel.DefaultOutputFormat, piaBusiness.OutputFormat);
    }

    [Fact]
    public void AllBuiltInPersonas_DefineANonEmptyOutputFormat()
    {
        // Every shipped persona declares its own output format (Pia uses the default text verbatim).
        Assert.All(BuiltInPersonas.All, p => Assert.False(string.IsNullOrWhiteSpace(p.OutputFormat)));
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
