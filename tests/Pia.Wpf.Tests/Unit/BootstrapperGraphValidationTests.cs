using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Pia;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

/// <summary>
/// Guards the production DI graph the way DEBUG startup does (<c>ValidateOnBuild</c> +
/// <c>ValidateScopes</c> in <c>Bootstrapper.InitializeAsync</c>): every registered
/// implementation's constructor dependencies must themselves be registered, and no
/// singleton may capture a scoped service. A missing concrete registration (e.g.
/// <c>HeadlessTurnExecutor</c> depending on the concrete <c>BackgroundAssistantTurnRunner</c>
/// while only the interface was registered) otherwise only surfaces as a crash on launch.
/// </summary>
public class BootstrapperGraphValidationTests
{
    [Fact]
    public void ProductionServiceGraph_ResolvesAndRespectsScopes()
    {
        var configure = typeof(Bootstrapper).GetMethod(
            "ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(configure);

        var services = new ServiceCollection();
        configure!.Invoke(null, [services]);

        // Throws AggregateException listing every unconstructable descriptor / captive dependency.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
