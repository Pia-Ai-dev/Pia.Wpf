using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class MvvmPatternTests
{
    [Fact]
    public void ViewModelClasses_MustInherit_ObservableObject()
    {
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .Should().Inherit(typeof(ObservableObject))
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"all ViewModel classes must inherit ObservableObject, but these don't: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void ViewModel_InjectedFields_MustBeReadonly()
    {
        var viewModelTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().Inherit(typeof(ObservableObject))
            .GetTypes();

        var violations = new List<string>();

        foreach (var vmType in viewModelTypes)
        {
            var nonReadonlyInterfaceFields = vmType
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(f => f.FieldType.IsInterface && !f.IsInitOnly)
                // Exclude compiler-generated backing fields for ObservableProperty
                .Where(f => !f.GetCustomAttributes<System.Runtime.CompilerServices.CompilerGeneratedAttribute>().Any())
                .ToList();

            foreach (var field in nonReadonlyInterfaceFields)
            {
                violations.Add($"{vmType.Name}.{field.Name} ({field.FieldType.Name})");
            }
        }

        Assert.True(violations.Count == 0,
            $"injected interface fields in ViewModels must be readonly, but these aren't: {string.Join(", ", violations)}");
    }
}
