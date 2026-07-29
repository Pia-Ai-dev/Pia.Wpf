using System.Reflection;
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

        Assert.NotNull(configureMethod);

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

        // The meeting-attendee interfaces live in their own namespace (Pia.Services.MeetingAttendee), so
        // enumerate them explicitly — otherwise IMeetingAttendeeService / IBrowserProvisioner would pass
        // this test only by accident of namespace, never actually verifying their DI registration.
        var meetingAttendeeInterfaces = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(MeetingAttendeeNamespace)
            .And().AreInterfaces()
            .GetTypes();

        var allInterfaces = serviceInterfaces.Concat(e2eeInterfaces).Concat(meetingAttendeeInterfaces);

        // INativeHotkeyService is created by INativeHotkeyServiceFactory, not registered directly.
        // IPluginToolHandler implementations are created by PluginService based on plugin kind.
        // IMeetingSession is created by MeetingAttendeeService at runtime with the provisioned Chromium
        // path (it has no parameterless seam), so it is intentionally not container-registered.
        // IAgentTurnExecutor is never a container service type: both implementations are constructed
        // explicitly because each is bound to something the container does not own. HeadlessTurnExecutor is
        // registered as its CONCRETE type and resolved from a fresh per-run scope; LiveTurnExecutor is
        // new'd by ChatSessionManager on the UI thread, bound to one ChatSession, and lives in
        // Pia.ViewModels.Models. (This interface only became visible to this test when the agent-spine
        // interface files were moved into Pia.Services.Interfaces — before that they declared the parent
        // namespace and escaped the sweep entirely.)
        var factoryCreated = new HashSet<string> { "INativeHotkeyService", "IPluginToolHandler", "IOptimizeFastPathHandle", "IMeetingSession", "IAgentTurnExecutor" };

        var unregistered = allInterfaces
            .Where(i => !factoryCreated.Contains(i.Name))
            .Where(i => !registeredServiceTypes.Contains(i))
            .Select(i => i.Name)
            .ToList();

        Assert.True(unregistered.Count == 0,
            $"all service interfaces must be registered in DI, but these are missing: {string.Join(", ", unregistered)}");
    }
}
