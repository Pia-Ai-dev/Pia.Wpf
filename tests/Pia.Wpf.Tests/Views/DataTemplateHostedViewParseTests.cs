using System.Windows;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

// The walk cannot reach DataTemplate content, Style.Triggers, or bindings with RelativeSource/ElementName/Source.
[Collection("WpfApplicationStatic")]
public class DataTemplateHostedViewParseTests
{
    // TodoPanelControl nulls its DataContext in the ctor and gets a TodoViewModel at Loaded, which the markup
    // cannot express — without this map the panel's paths read UNRESOLVED against the hosting ViewModel.
    internal static readonly Dictionary<Type, Type> CodeAssignedRoots =
        new() { [typeof(Pia.Views.TodoPanelControl)] = typeof(TodoViewModel) };

    // Floors, not counts: set well under the measured values so ordinary markup edits never touch this file.
    // OptimizeView is fully qualified because SettingsViews holds a different type of the same name.
    public static TheoryData<Type, Type, int> Hosted => new()
    {
        { typeof(AssistantHistoryViewModel), typeof(Pia.Views.AssistantHistoryView), 14 },
        { typeof(HistoryViewModel), typeof(Pia.Views.HistoryView), 18 },
        { typeof(MemoryViewModel), typeof(Pia.Views.MemoryView), 19 },
        { typeof(OptimizeViewModel), typeof(Pia.Views.OptimizeView), 20 },
        { typeof(RemindersViewModel), typeof(Pia.Views.RemindersView), 8 },
        { typeof(SettingsViewModel), typeof(Pia.Views.SettingsView), 140 },
        { typeof(TodoViewModel), typeof(Pia.Views.TodoView), 10 },
    };

    [Theory]
    [MemberData(nameof(Hosted))]
    public void EveryBindingPath_ResolvesOnTheViewModelItsAppXamlTemplateIsKeyedOn(
        Type viewModel, Type view, int minimumBoundPaths)
    {
        var (produced, bindings) = WpfStaHost.Run(() =>
        {
            // A missing key is a null template, so it becomes a named failure rather than a bare NRE.
            if (Application.Current.Resources[new DataTemplateKey(viewModel)] is not DataTemplate template)
                return ("<no DataTemplate keyed on this ViewModel in App.xaml>", Array.Empty<string>());

            var content = template.LoadContent();
            return content is DependencyObject element
                ? (content.GetType().FullName!, BindingPathWalker.Describe(element, viewModel, CodeAssignedRoots))
                : (content?.GetType().FullName ?? "<null>", Array.Empty<string>());
        });

        Assert.True(produced == view.FullName,
            $"App.xaml's DataTemplate for {viewModel.Name} produces {produced}, not {view.FullName} — so " +
            "either the template was re-typed, re-keyed or removed, and every binding path below would be " +
            "walked against a ViewModel that no longer hosts this view.");

        Assert.True(bindings.Length >= minimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed {view.Name}, which is below the " +
            $"non-vacuity floor of {minimumBoundPaths}. The walk is LOGICAL, so suspect a container that no " +
            "longer reports logical children rather than a genuine removal.");

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            $"these Binding paths in {view.FullName} do not resolve to a public property on the ViewModel the " +
            "markup roots them at, so they bind to nothing and fail silently at runtime: " +
            string.Join(", ", unresolved));
    }

    [Fact]
    public void TheTopLevelOptimizeView_ReachesTheTodoPanelsPathsThroughTheCodeAssignedReRoot()
    {
        // Both halves are needed: either assertion alone can stay green while the re-root is broken.
        var bindings = WpfStaHost.Run(() =>
        {
            var template = (DataTemplate)Application.Current.Resources[new DataTemplateKey(typeof(OptimizeViewModel))];
            return BindingPathWalker.Describe(
                (DependencyObject)template.LoadContent(), typeof(OptimizeViewModel), CodeAssignedRoots);
        });

        Assert.Contains(bindings, b => b.Contains("<code-assigned TodoViewModel>"));
        Assert.Contains(bindings, b => b.Contains("=AddTodoCommand [TodoViewModel]"));
    }
}
