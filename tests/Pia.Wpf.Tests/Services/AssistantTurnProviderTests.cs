using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AssistantTurnProviderTests
{
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly AiProvider _default = new() { Name = "Default", Endpoint = "https://default.test" };
    private readonly AiProvider _local = new() { Name = "Local", Endpoint = "http://localhost:11434" };

    public AssistantTurnProviderTests() =>
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(_default);

    [Fact]
    public async Task ResolveAsync_PrefersThePersonasProvider()
    {
        _providers.GetProviderAsync(_local.Id).Returns(_local);

        var provider = await AssistantTurnProvider.ResolveAsync(
            new Persona { Name = "Local", SystemPrompt = "x", PreferredProviderId = _local.Id }, _providers);

        Assert.Same(_local, provider);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackWhenThePreferredProviderIsGone()
    {
        var provider = await AssistantTurnProvider.ResolveAsync(
            new Persona { Name = "Local", SystemPrompt = "x", PreferredProviderId = Guid.NewGuid() }, _providers);

        Assert.Same(_default, provider);
    }

    [Fact]
    public async Task ResolveAsync_WithoutAPersona_UsesTheAssistantDefault() =>
        Assert.Same(_default, await AssistantTurnProvider.ResolveAsync(null, _providers));
}
