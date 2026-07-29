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
        // NetArchTest's AreClasses() does NOT exclude enums or private nested types, so the raw sweep flagged
        // RunProgressState (an enum), plus RunProgressViewModel.Ledger and .StepLedgerEntry (both
        // `private sealed class` implementation details) as "ViewModels that must inherit ObservableObject".
        // None of the three is a ViewModel. Filter in LINQ the way NamingConventionTests already does, and
        // scope the rule to types actually NAMED ViewModel — the companion test above asserts the converse
        // (every ObservableObject subclass here ends with "ViewModel"), so together they still close the loop
        // without either one guessing about helper types.
        var violations = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().AreClasses()
            .And().AreNotAbstract()
            .GetTypes()
            .Where(t => !t.IsValueType && !t.IsNested)
            .Where(t => t.Name.EndsWith("ViewModel"))
            .Where(t => !typeof(ObservableObject).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(violations.Count == 0,
            $"all ViewModel classes must inherit ObservableObject, but these don't: {string.Join(", ", violations)}");
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
