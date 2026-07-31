using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia;
using Pia.Services;
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

    /// <summary>
    /// The one thing <c>ValidateOnBuild</c> above cannot see: a factory descriptor's LAMBDA BODY.
    /// <c>Func&lt;StepPersonaResolver&gt;</c> (Batch 07 G6) is a singleton that calls
    /// <c>GetRequiredService</c> on the provider that resolved it — the ROOT — so if the resolver ever gained a
    /// <c>Scoped</c> dependency, every interactive agent run would throw at its first step with
    /// <c>ValidateScopes</c> on, and nothing would fail until then. Checked statically here, without resolving
    /// anything: real construction of these services touches settings files and SQLite.
    /// <para>
    /// The factory exists because the resolver's memo must last exactly one RUN while both of its non-headless
    /// consumers outlive one (a <c>Scoped</c> <c>ChatSessionManager</c> and the planner inside it).
    /// </para>
    /// </summary>
    [Fact]
    public void TheStepPersonaResolverFactory_IsRootSafe()
    {
        var services = BuildConfiguredServices();

        var factory = Assert.Single(services, d => d.ServiceType == typeof(Func<StepPersonaResolver>));
        Assert.Equal(ServiceLifetime.Singleton, factory.Lifetime);

        var resolver = Assert.Single(services, d => d.ServiceType == typeof(StepPersonaResolver));
        // Transient, so each invocation of the factory really does hand back a fresh memo (07 D6).
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
