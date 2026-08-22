using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The one persona ladder and the one effort ladder both dispatch legs resolve a routine's pins
/// through.</summary>
public sealed class RunPinResolverTests
{
    private static readonly Persona _modePersona = new() { Name = "Mode", SystemPrompt = "sys" };

    private static IPersonaService Personas(params Persona[] available)
    {
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(_modePersona);
        personas.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>(available));
        return personas;
    }

    private static Task<Persona> ResolveAsync(IPersonaService personas, Guid? pin) =>
        RunPinResolver.ResolvePersonaAsync(personas, pin, UserOperatingMode.Personal, NullLogger.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoPin_TakesTheModePersona_WithoutReadingTheList(bool asEmptyGuid)
    {
        var personas = Personas();

        var resolved = await ResolveAsync(personas, asEmptyGuid ? Guid.Empty : null);

        Assert.Same(_modePersona, resolved);
        await personas.DidNotReceive().GetPersonasAsync();
    }

    /// <summary>The decisive difference from a planner-assigned id: nothing here consults the roster, and the
    /// pin is matched against the block-list-filtered list rather than <c>GetPersonaAsync</c>.</summary>
    [Fact]
    public async Task APinnedPersona_IsHonoured_FromTheFilteredList()
    {
        var pinned = new Persona { Name = "Specialist", SystemPrompt = "sys" };
        var personas = Personas(pinned);

        var resolved = await ResolveAsync(personas, pinned.Id);

        Assert.Same(pinned, resolved);
        await personas.DidNotReceive().GetPersonaAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task ADanglingPin_FallsBackToTheModePersona_RatherThanFailing()
    {
        var personas = Personas(new Persona { Name = "Someone else", SystemPrompt = "sys" });

        Assert.Same(_modePersona, await ResolveAsync(personas, Guid.NewGuid()));
    }

    [Fact]
    public async Task APersonaStoreThatThrows_FallsBackToTheModePersona()
    {
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(_modePersona);
        personas.GetPersonasAsync().ThrowsAsync(new InvalidOperationException("store is down"));

        Assert.Same(_modePersona, await ResolveAsync(personas, Guid.NewGuid()));
    }

    private static AiProvider NewProvider() => new()
    {
        Id = Guid.NewGuid(), Name = "P", Endpoint = "https://example", ProviderType = AiProviderType.OpenAI,
    };

    [Fact]
    public void NoEffortAnywhere_HandsBackTheSameInstance()
    {
        var provider = NewProvider();

        Assert.Same(provider, RunPinResolver.ApplyEffort(provider, null, null));
    }

    [Fact]
    public void TheJobsPin_BeatsThePersonas()
    {
        var provider = NewProvider();

        var stamped = RunPinResolver.ApplyEffort(provider, ReasoningEffort.Minimal, ReasoningEffort.High);

        Assert.Equal(ReasoningEffort.Minimal, stamped.ReasoningEffort);
        // Cloned, not mutated: AiProvider instances come out of a shared store.
        Assert.NotSame(provider, stamped);
        Assert.Null(provider.ReasoningEffort);
    }

    [Fact]
    public void ThePersonasEffort_AppliesWhenTheJobPinsNothing()
    {
        var stamped = RunPinResolver.ApplyEffort(NewProvider(), null, ReasoningEffort.High);

        Assert.Equal(ReasoningEffort.High, stamped.ReasoningEffort);
    }

    /// <summary><c>ReasoningEffort.None</c> means "no reasoning" — a real thing to pin, and it must not be
    /// mistaken for "no pin".</summary>
    [Fact]
    public void NoneIsAValue_NotAnAbsence()
    {
        var stamped = RunPinResolver.ApplyEffort(NewProvider(), ReasoningEffort.None, ReasoningEffort.High);

        Assert.Equal(ReasoningEffort.None, stamped.ReasoningEffort);
    }
}
