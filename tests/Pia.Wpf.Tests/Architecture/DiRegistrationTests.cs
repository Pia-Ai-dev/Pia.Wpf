using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class DiRegistrationTests
{
    private static IServiceCollection BuildServiceCollection()
    {
        var configureMethod = typeof(Bootstrapper).GetMethod(
            "ConfigureServices",
            BindingFlags.NonPublic | BindingFlags.Static);

        configureMethod.Should().NotBeNull("Bootstrapper.ConfigureServices must exist");

        var services = new ServiceCollection();
        configureMethod!.Invoke(null, [services]);
        return services;
    }

    [Fact]
    public void AllServiceInterfaces_MustHaveRegisteredImplementation()
    {
        var services = BuildServiceCollection();
        var registeredServiceTypes = services
            .Select(sd => sd.ServiceType)
            .ToHashSet();

        // Discover all interfaces in Services/Interfaces and Services/E2EE
        var serviceInterfaces = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServiceInterfacesNamespace)
            .And().AreInterfaces()
            .GetTypes();

        var e2eeInterfaces = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(E2EENamespace)
            .And().AreInterfaces()
            .GetTypes();

        var allInterfaces = serviceInterfaces.Concat(e2eeInterfaces);

        // INativeHotkeyService is created by INativeHotkeyServiceFactory, not registered directly
        var factoryCreated = new HashSet<string> { "INativeHotkeyService" };

        var unregistered = allInterfaces
            .Where(i => !factoryCreated.Contains(i.Name))
            .Where(i => !registeredServiceTypes.Contains(i))
            .Select(i => i.Name)
            .ToList();

        unregistered.Should().BeEmpty(
            "all service interfaces must be registered in DI, but these are missing: {0}",
            string.Join(", ", unregistered));
    }
}
