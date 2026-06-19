using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class NamingConventionTests
{
    [Fact]
    public void ViewModels_MustEndWith_ViewModel()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().Inherit(typeof(ObservableObject))
            .Should().HaveNameEndingWith("ViewModel")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"ObservableObject subclasses in ViewModels must end with 'ViewModel', but these don't: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void ServiceClasses_MustFollowNamingConvention()
    {
        var allowedSuffixes = new[] { "Service", "Handler", "Mapper", "Parser", "Detector", "Factory", "Client", "Buffer", "Engine" };

        // Domain-named service/helper classes that legitimately do not carry one of the suffixes above.
        // ChromiumProvisioner / TeamsMeetingSession / AudioHopResampler are agent-noun / stateful helpers
        // named by domain convention rather than the generic suffix list; ChromiumDownloadProgress is a
        // progress-DTO record mirroring ModelDownloadProgress (which lives under Services.Interfaces and
        // is therefore already excluded). Keep this list narrow — do not exclude whole namespaces, so
        // future non-conforming service classes are still caught.
        var exemptNames = new HashSet<string>
        {
            "ChromiumProvisioner",
            "TeamsMeetingSession",
            "AudioHopResampler",
            "ChromiumDownloadProgress",
        };

        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().DoNotResideInNamespace(ServiceInterfacesNamespace)
            .And().DoNotResideInNamespace(E2EENamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .GetTypes();

        var violations = serviceTypes
            // The convention is about service *classes*: enums and Win32 interop structs (value types,
            // e.g. AUDIOCLIENT_ACTIVATION_PARAMS / WAVEFORMATEX, which must mirror the native API names)
            // are not services and are excluded.
            .Where(t => !t.IsValueType)
            .Where(t => !t.IsNestedPrivate && !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
            .Where(t => !exemptNames.Contains(t.Name))
            .Where(t => !allowedSuffixes.Any(suffix => t.Name.EndsWith(suffix)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(violations.Count == 0,
            $"service classes must end with one of [{string.Join(", ", allowedSuffixes)}], but these don't: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ServiceInterfaces_MustStartWith_I()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServiceInterfacesNamespace)
            .And().AreInterfaces()
            .Should().HaveNameStartingWith("I")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"service interfaces must start with 'I', but these don't: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void Converters_MustEndWith_Converter()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ConvertersNamespace)
            .And().AreClasses()
            .Should().HaveNameEndingWith("Converter")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"converter classes must end with 'Converter', but these don't: {FormatFailingTypes(result)}");
    }
}
