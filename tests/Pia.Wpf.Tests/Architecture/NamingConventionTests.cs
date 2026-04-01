using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAssertions;
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

        result.IsSuccessful.Should().BeTrue(
            "ObservableObject subclasses in ViewModels must end with 'ViewModel', but these don't: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void ServiceClasses_MustFollowNamingConvention()
    {
        var allowedSuffixes = new[] { "Service", "Handler", "Mapper", "Parser", "Detector", "Factory" };

        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().DoNotResideInNamespace(ServiceInterfacesNamespace)
            .And().DoNotResideInNamespace(E2EENamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .GetTypes();

        var violations = serviceTypes
            .Where(t => !t.IsNestedPrivate && !t.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
            .Where(t => !allowedSuffixes.Any(suffix => t.Name.EndsWith(suffix)))
            .Select(t => t.Name)
            .ToList();

        violations.Should().BeEmpty(
            "service classes must end with one of [{0}], but these don't: {1}",
            string.Join(", ", allowedSuffixes),
            string.Join(", ", violations));
    }

    [Fact]
    public void ServiceInterfaces_MustStartWith_I()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServiceInterfacesNamespace)
            .And().AreInterfaces()
            .Should().HaveNameStartingWith("I")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "service interfaces must start with 'I', but these don't: {0}",
            FormatFailingTypes(result));
    }

    [Fact]
    public void Converters_MustEndWith_Converter()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ConvertersNamespace)
            .And().AreClasses()
            .Should().HaveNameEndingWith("Converter")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "converter classes must end with 'Converter', but these don't: {0}",
            FormatFailingTypes(result));
    }
}
