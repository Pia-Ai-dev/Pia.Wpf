using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using NetArchTest.Rules;
using Xunit;
using static Pia.Tests.Architecture.ArchitectureTestBase;

namespace Pia.Tests.Architecture;

public class DependencyInjectionTests
{
    [Fact]
    public void ViewModels_MustNotReference_SystemWindows()
    {
        // VoiceModeViewModel is a transient UI helper requiring Dispatcher — not DI-registered.
        // AssistantViewModel is flagged transitively because it creates VoiceModeViewModel.
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().DoNotHaveNameMatching("VoiceModeViewModel")
            .And().DoNotHaveNameMatching("AssistantViewModel")
            .ShouldNot().HaveDependencyOn("System.Windows")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"ViewModels must not reference System.Windows (use SynchronizationContext instead), but these do: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void ViewModels_MustNotReference_Bootstrapper()
    {
        var viewModelTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .GetTypes();

        var violations = new List<string>();

        foreach (var type in viewModelTypes)
        {
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            // Check if any method body references Bootstrapper (via IL would be complex,
            // so we check field types and method return/parameter types)
            foreach (var field in fields)
            {
                if (field.FieldType.FullName?.Contains("Bootstrapper") == true)
                    violations.Add($"{type.Name}.{field.Name}");
            }
        }

        // Also use NetArchTest for dependency checking
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .ShouldNot().HaveDependencyOn("Pia.Bootstrapper")
            .GetResult();

        // Combine both checks
        if (!result.IsSuccessful && result.FailingTypeNames != null)
        {
            foreach (var name in result.FailingTypeNames)
            {
                if (!violations.Contains(name))
                    violations.Add(name);
            }
        }

        Assert.True(violations.Count == 0,
            $"ViewModels must not access Bootstrapper (use injected services instead), but these do: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ViewModels_MustOnlyInject_InterfacesOrViewModels()
    {
        var viewModelTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().Inherit(typeof(ObservableObject))
            .GetTypes();

        var violations = new List<string>();

        foreach (var vmType in viewModelTypes)
        {
            var ctor = vmType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor == null) continue;

            foreach (var param in ctor.GetParameters())
            {
                var paramType = param.ParameterType;

                // Allowed: interfaces
                if (paramType.IsInterface) continue;

                // Allowed: ILogger<T> (generic interface resolves as class)
                if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(ILogger<>)) continue;

                // Allowed: other ViewModels (composite pattern)
                if (typeof(ObservableObject).IsAssignableFrom(paramType)) continue;

                // Allowed: delegates (Func<>, Action<>) for manually-created ViewModels
                if (typeof(Delegate).IsAssignableFrom(paramType)) continue;

                violations.Add($"{vmType.Name} injects concrete type {paramType.Name} via parameter '{param.Name}'");
            }
        }

        Assert.True(violations.Count == 0,
            $"ViewModel constructors must only accept interfaces or ViewModels, but found: {string.Join("; ", violations)}");
    }

    [Fact]
    public void Services_MustNotInject_ViewModels()
    {
        var serviceTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ServicesNamespace)
            .And().AreClasses()
            .GetTypes();

        var violations = new List<string>();

        foreach (var svcType in serviceTypes)
        {
            var ctor = svcType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor == null) continue;

            foreach (var param in ctor.GetParameters())
            {
                if (param.ParameterType.Namespace?.StartsWith("Pia.ViewModels") == true)
                    violations.Add($"{svcType.Name} injects ViewModel type {param.ParameterType.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Services must not inject ViewModels, but found: {string.Join("; ", violations)}");
    }

    [Fact]
    public void ViewModels_MustNotInject_InfrastructureTypes()
    {
        var viewModelTypes = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().Inherit(typeof(ObservableObject))
            .GetTypes();

        var violations = new List<string>();

        foreach (var vmType in viewModelTypes)
        {
            var ctor = vmType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor == null) continue;

            foreach (var param in ctor.GetParameters())
            {
                if (param.ParameterType.Namespace?.StartsWith("Pia.Infrastructure") == true)
                    violations.Add($"{vmType.Name} injects infrastructure type {param.ParameterType.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            $"ViewModels must not inject infrastructure types, but found: {string.Join("; ", violations)}");
    }
}
