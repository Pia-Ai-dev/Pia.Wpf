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
        // MEASURED, not reasoned (Batch 12 Step 0): with every exemption removed this rule flags
        // AssistantViewModel, TranscriptOverlayViewModel and VoiceModeViewModel — and NOT
        // MeetingAttendeeViewModel, even with its base still dirty, because NetArchTest 1.3.2 does not
        // resolve base-type dependencies transitively.
        //
        // AssistantViewModel is flagged DIRECTLY, and not for the dispatcher. Its COMPLETE System.Windows
        // dependency set was measured with Mono.Cecil over the built assembly, and it has TWO roots:
        //   1. System.Windows.Media.Imaging.BitmapSource — the clipboard-image paste path (:7 using, the
        //      IAsyncRelayCommand<BitmapSource> property, its AsyncRelayCommand<BitmapSource>
        //      construction, and ExecuteHandleImagePasted(BitmapSource?)); member signatures only, no IL
        //      site. Call site of the paste: AssistantView.xaml.cs:153-154.
        //   2. System.Windows.Input.ICommand — two `callvirt ICommand::Execute(Object)` sites:
        //      OnMeetingAttendeeSummarizeRequested (:622 SendMessageCommand.Execute(null)) and
        //      CancelPendingActionCards (:832 card.CancelCommand.Execute(null)). Execute is declared on
        //      ICommand, so a cast to the toolkit's IRelayCommand does NOT avoid it; closing this one
        //      means calling the commands' own methods instead of going through the ICommand face.
        // NetArchTest matches dependencies by name prefix, so "System.Windows" catches both at full
        // type-name depth. BOTH must go before this exemption can be deleted — removing only the
        // BitmapSource path turns the rule red again, naming AssistantViewModel. The dispatcher ban is
        // still enforced for it, explicitly, by AssistantViewModel_MustNotReference_DispatcherOrApplication
        // below. (The comment this replaces claimed AssistantViewModel was flagged "transitively because
        // it creates VoiceModeViewModel". That was never the mechanism — and the comment that replaced
        // THAT one named only the BitmapSource half, which is the same class of error.)
        //
        // TranscriptOverlayViewModel and MeetingAttendeeViewModel were removed in Batch 12 Unit 2:
        // the base's DispatchToUi now goes through IUiDispatcher, so neither names System.Windows at
        // all — and MeetingAttendeeViewModel never did.
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
        // AssistantViewModel is exempt from the blanket System.Windows rule above, but only for
        // BitmapSource and ICommand (both roots measured — see that rule's comment) and NEVER for the
        // Dispatcher or Application. Keep the dispatcher ban enforced for it explicitly, so nobody
        // reintroduces App.Current.Dispatcher under cover of that exemption. Measured before the
        // migration: this failed, naming AssistantViewModel — so it is not vacuous. Measured as an
        // instrument after it: the same two prefixes flag OutputService and UiDispatcherService, which do
        // read Application.Current.Dispatcher.
        var target = Types.InAssembly(PiaAssembly)
            .That().ResideInNamespace(ViewModelsNamespace)
            .And().HaveName("AssistantViewModel")
            .GetTypes();

        // NetArchTest reports success on an EMPTY type set (measured), so without this guard a rename
        // would silently turn the assertion below green.
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

                // Allowed: value types and string — DATA, not a dependency. This rule exists to stop a
                // ViewModel taking a concrete SERVICE (which defeats substitution and pins it to one
                // implementation); a Guid identifying which run a per-run ViewModel represents is an
                // argument, not something DI could ever have supplied. RunProgressViewModel(Guid runId)
                // is constructed on the UI thread by AssistantViewModel, not resolved from the container.
                if (paramType.IsValueType || paramType == typeof(string)) continue;

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
