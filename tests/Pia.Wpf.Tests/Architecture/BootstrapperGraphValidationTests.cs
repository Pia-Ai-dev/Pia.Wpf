using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>Runs the DI validation DEBUG startup does, so a missing concrete registration or a captured scoped
/// service fails here rather than as a crash on launch.</summary>
public class BootstrapperGraphValidationTests
{
    [Fact]
    public void ProductionServiceGraph_ResolvesAndRespectsScopes()
    {
        var services = BuildConfiguredServices();

        // Throws AggregateException listing every unconstructable descriptor / captive dependency.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    /// <summary>The production DI registrations, built by invoking <c>Bootstrapper.ConfigureServices</c> directly.</summary>
    private static ServiceCollection BuildConfiguredServices()
    {
        var configure = typeof(Bootstrapper).GetMethod(
            "ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(configure);

        var services = new ServiceCollection();
        configure!.Invoke(null, [services]);
        return services;
    }

    /// <summary>What <c>ValidateOnBuild</c> cannot see is a factory descriptor's LAMBDA BODY: this singleton
    /// resolves from the ROOT, so a <c>Scoped</c> dependency would only throw at an agent run's first step.</summary>
    [Fact]
    public void TheStepPersonaResolverFactory_IsRootSafe()
    {
        var services = BuildConfiguredServices();

        var factory = Assert.Single(services, d => d.ServiceType == typeof(Func<StepPersonaResolver>));
        Assert.Equal(ServiceLifetime.Singleton, factory.Lifetime);

        var resolver = Assert.Single(services, d => d.ServiceType == typeof(StepPersonaResolver));
        // Transient, so each invocation of the factory really does hand back a fresh memo.
        Assert.Equal(ServiceLifetime.Transient, resolver.Lifetime);

        var parameters = typeof(StepPersonaResolver).GetConstructors().Single().GetParameters();
        Assert.NotEmpty(parameters);   // non-vacuity: an empty ctor would make the loop below assert nothing
        foreach (var p in parameters)
        {
            // ILogger<T> is provided by the logging builder rather than a plain descriptor; everything else
            // must be registered and must not be Scoped.
            if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
                continue;
            var dependency = Assert.Single(services, d => d.ServiceType == p.ParameterType);
            Assert.NotEqual(ServiceLifetime.Scoped, dependency.Lifetime);
        }
    }
}
