using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class AsyncSafetyTests
{
    private static List<string> FindAsyncVoidMethods(IEnumerable<Type> types, HashSet<string>? allowedMethodNames = null)
    {
        var violations = new List<string>();

        foreach (var type in types)
        {
            // Skip compiler-generated types (async lambdas, closures)
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                continue;

            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly);

            var asyncVoidMethods = methods
                .Where(m => m.ReturnType == typeof(void))
                .Where(m => m.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
                // Exclude compiler-generated methods (async lambdas, local functions)
                .Where(m => !m.Name.Contains('<') && !m.Name.Contains('>'))
                .Where(m => allowedMethodNames == null || !allowedMethodNames.Contains(m.Name))
                .ToList();

            foreach (var method in asyncVoidMethods)
            {
                violations.Add($"{type.Name}.{method.Name}");
            }
        }

        return violations;
    }

    [Fact]
    public void Services_MustNotHave_AsyncVoidMethods()
    {
        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().AreClasses()
            .GetTypes();

        var violations = FindAsyncVoidMethods(serviceTypes);

        violations.Should().BeEmpty(
            "service classes must not have async void methods (use async Task instead): {0}",
            string.Join(", ", violations));
    }

    [Fact]
    public void ViewModels_AsyncVoid_MustBeLimitedToKnownPatterns()
    {
        // These are acceptable async void patterns in ViewModels:
        // - Event handlers (OnNavigatedTo, etc.) that implement framework interfaces
        var allowedNames = new HashSet<string> { "OnNavigatedTo", "OnNavigatedFrom" };

        var viewModelTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().AreClasses()
            .GetTypes();

        var violations = FindAsyncVoidMethods(viewModelTypes, allowedNames);

        violations.Should().BeEmpty(
            "ViewModel async void methods must be limited to known patterns (OnNavigatedTo, OnNavigatedFrom), " +
            "but found unexpected async void methods: {0}",
            string.Join(", ", violations));
    }

    [Fact]
    public void Infrastructure_MustNotHave_AsyncVoidMethods()
    {
        var infraTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(InfrastructureNamespace)
            .And().AreClasses()
            .GetTypes();

        var violations = FindAsyncVoidMethods(infraTypes);

        violations.Should().BeEmpty(
            "infrastructure classes must not have async void methods: {0}",
            string.Join(", ", violations));
    }
}
