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
        // A service named for what it DOES (Planner/Orchestrator/Verifier) is as conventional as one
        // suffixed "Service"; suffixing those "…Service" would name the layer instead of the thing.
        var allowedSuffixes = new[]
        {
            "Service", "Handler", "Mapper", "Parser", "Detector", "Factory", "Client", "Engine",
            "Calculator", "Resolver", "Surface", "Buffer", "Builder", "Composer", "Store", "Runner",
            "Indexer", "Watcher", "Renderer", "Clusterer", "Extractor", "Collector",
            "Planner", "Orchestrator", "Coordinator", "Verifier", "Launcher", "Executor", "Context",
            "Provisioner", "Session", "Resampler", "Reconciler", "Pool", "Throttle",
        };

        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().DoNotResideInNamespace(ServiceInterfacesNamespace)
            .And().DoNotResideInNamespace(ServiceExceptionsNamespace)
            .And().DoNotResideInNamespace(E2EENamespace)
            .And().DoNotResideInNamespace(ConsentNamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .GetTypes();

        var violations = serviceTypes
            // Value types are data carriers, not services: Win32 interop structs must mirror the native API names.
            .Where(t => !t.IsValueType && !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
            // A nested type is named by its containing type, which already carries the naming burden.
            .Where(t => !t.IsNested)
            // Records are data carriers by definition; where they may LIVE is a separate, structural question,
            // asserted by RecordTypes_MustNotLiveInTheServicesRootNamespace below.
            .Where(t => !IsRecord(t))
            .Where(t => !allowedSuffixes.Any(suffix => t.Name.EndsWith(suffix)))
            .Select(t => t.Name)
            .ToList();

        Assert.True(violations.Count == 0,
            $"service classes must end with one of [{string.Join(", ", allowedSuffixes)}], but these don't: {string.Join(", ", violations)}");
    }

    /// <summary>The root <c>Pia.Services</c> namespace is for services, so a record declared there is misfiled;
    /// feature sub-namespaces are deliberately out of scope.</summary>
    [Fact]
    public void RecordTypes_MustNotLiveInTheServicesRootNamespace()
    {
        var misfiled = PiaAssembly.GetTypes()
            .Where(t => t.Namespace == ServicesNamespace)
            .Where(t => !t.IsNested && !t.IsValueType && IsRecord(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(misfiled.Count == 0,
            "records in the Pia.Services root namespace are misfiled — move a contract to Pia.Services.Interfaces "
            + $"or a feature DTO to its feature sub-namespace: {string.Join(", ", misfiled)}");
    }

    /// <summary>The compiler-synthesised <c>&lt;Clone&gt;$</c> member is the only reliable IL-level record marker
    /// — there is no <c>Type.IsRecord</c>.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

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
        // Enums and compiler-generated helpers in the namespace are not converters.
        var converterTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ConvertersNamespace)
            .And().AreClasses()
            .GetTypes();

        var violations = converterTypes
            .Where(t => !t.IsEnum)
            .Where(t => !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
            .Where(t => !t.Name.EndsWith("Converter"))
            .Select(t => t.Name)
            .ToList();

        Assert.True(violations.Count == 0,
            $"converter classes must end with 'Converter', but these don't: {string.Join(", ", violations)}");
    }
}
