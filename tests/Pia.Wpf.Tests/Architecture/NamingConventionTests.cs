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
        var allowedSuffixes = new[] { "Service", "Handler", "Mapper", "Parser", "Detector", "Factory", "Client", "Engine", "Calculator", "Resolver", "Surface", "Builder", "Composer", "Indexer", "Watcher", "Renderer", "Runner", "Store" };

        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().DoNotResideInNamespace(ServiceInterfacesNamespace)
            .And().DoNotResideInNamespace(ServiceExceptionsNamespace)
            .And().DoNotResideInNamespace(E2EENamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .GetTypes();

        var violations = serviceTypes
            // Value types (e.g. the ambient context record struct TaskContext) are data carriers,
            // not service classes — exclude them. This only ever narrows scrutiny; a misnamed
            // reference-type service is still caught.
            .Where(t => !t.IsNestedPrivate && !t.IsValueType && !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
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
        // The naming convention targets actual converter classes. Enums and
        // compiler-generated helpers in the namespace (e.g. converter-config
        // enums nested or alongside their converter) are not converters and
        // must not be forced to end in 'Converter'.
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
