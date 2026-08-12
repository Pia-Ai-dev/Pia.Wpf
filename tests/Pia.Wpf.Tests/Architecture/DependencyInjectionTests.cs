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
        // AssistantViewModel is exempt because it still names System.Windows through BitmapSource and ICommand;
        // both roots must go before the exemption can, and its dispatcher ban is enforced by the fact below.
        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().DoNotResideInNamespace(ViewModelModelsNamespace)
            .And().DoNotHaveNameMatching("AssistantViewModel")
            .ShouldNot().HaveDependencyOn("System.Windows")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"ViewModels must not reference System.Windows (use SynchronizationContext instead), but these do: {FormatFailingTypes(result)}");
    }

    [Fact]
    public void AssistantViewModel_MustNotReference_DispatcherOrApplication()
    {
        // Enforced explicitly so the exemption above cannot be used to reintroduce App.Current.Dispatcher.
        var target = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().HaveName("AssistantViewModel")
            .GetTypes();

        // NetArchTest reports success on an empty type set, so a rename would silently green the assertion below.
        Assert.Single(target);

        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().HaveName("AssistantViewModel")
            .ShouldNot().HaveDependencyOnAny("System.Windows.Threading", "System.Windows.Application")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"AssistantViewModel must not reference the WPF Dispatcher or Application (inject IUiDispatcher), but it does: {FormatFailingTypes(result)}");
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

            // Signatures only — catching a method BODY would need an IL scan.
            foreach (var field in fields)
            {
                if (field.FieldType.FullName?.Contains("Bootstrapper") == true)
                    violations.Add($"{type.Name}.{field.Name}");
            }
        }

        var result = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .ShouldNot().HaveDependencyOn("Pia.Bootstrapper")
            .GetResult();

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

                if (paramType.IsInterface) continue;

                // A closed ILogger<T> reflects as a generic class, not an interface.
                if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(ILogger<>)) continue;

                // Composing ViewModels is allowed.
                if (typeof(ObservableObject).IsAssignableFrom(paramType)) continue;

                if (typeof(Delegate).IsAssignableFrom(paramType)) continue;

                // Value types and string are data, not dependencies — a per-run ViewModel's runId is an argument
                // DI could never have supplied.
                if (paramType.IsValueType || paramType == typeof(string)) continue;

                // The BCL's clock abstraction: registered in DI and substituted in tests like any interface,
                // it just isn't spelled with an I.
                if (paramType == typeof(TimeProvider)) continue;

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
