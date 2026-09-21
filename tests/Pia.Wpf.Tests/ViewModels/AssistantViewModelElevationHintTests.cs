using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The probe is always stubbed — asserting the real one would flip the moment the test host itself runs
/// elevated.
/// </summary>
public sealed class AssistantViewModelElevationHintTests
{
    private static IElevationService Probe(bool isElevated)
    {
        var probe = Substitute.For<IElevationService>();
        probe.IsElevated.Returns(isElevated);
        return probe;
    }

    [Fact]
    public void AnElevatedSessionShowsTheHint()
    {
        var visible = WpfStaHost.Run(
            () => AssistantViewModelBuilder.Create(elevation: Probe(true)).IsElevatedSessionHintVisible);

        Assert.True(visible);
    }

    [Fact]
    public void AnOrdinarySessionShowsNoHint()
    {
        var visible = WpfStaHost.Run(
            () => AssistantViewModelBuilder.Create(elevation: Probe(false)).IsElevatedSessionHintVisible);

        Assert.False(visible);
    }

    [Fact]
    public void NoProbeShowsNoHint()
    {
        var visible = WpfStaHost.Run(() => AssistantViewModelBuilder.Create().IsElevatedSessionHintVisible);

        Assert.False(visible);
    }

    [Fact]
    public void DismissHidesTheHint()
    {
        var visible = WpfStaHost.Run(() =>
        {
            var vm = AssistantViewModelBuilder.Create(elevation: Probe(true));
            vm.DismissElevatedSessionHintCommand.Execute(null);
            return vm.IsElevatedSessionHintVisible;
        });

        Assert.False(visible);
    }
}
