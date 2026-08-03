using System.Windows;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The seven top-level views hosted by an <c>App.xaml</c> <c>DataTemplate</c>, none of which any test parsed
/// before Batch 15. Their binding paths are walked against the ViewModel type the template is KEYED on.
/// <para>
/// <b>The host guard is stronger here than the reflection recipe the settings views use, and that is the
/// point of doing them this way.</b> <see cref="ViewHostDataContextTests"/> exists because a per-view fact
/// that reflects its root off a property NAME cannot see a <c>DataContext</c> re-host — proven by execution
/// in the Batch 14 review. These seven have no such hole to close, because the host relationship is not a
/// property name to reflect off: it is a resource, and this fact READS it. Look up
/// <c>Application.Current.Resources[new DataTemplateKey(vm)]</c>, <c>LoadContent()</c> it, and assert the
/// object that comes out is the expected view. A re-typed, re-keyed or deleted <c>DataTemplate</c> reds here,
/// and the view the walk then examines is the one the template really produces — so the host check and the
/// parse cannot drift apart by construction.
/// </para>
/// <para>
/// <b>Amends this batch's own decision D1 ("one file per view"), on purpose.</b> Seven near-identical files
/// would duplicate the lookup seven times; a <c>[Theory]</c> keeps the floors in one table and still names
/// the failing view in the test case. The three views with a DIFFERENT technique each keep their own file.
/// </para>
/// <para>
/// Out of reach for these walks, in the same way and for the same reason as every other file in this folder:
/// <c>DataTemplate</c> content, <c>Style.Triggers</c>, and bindings carrying <c>RelativeSource</c>,
/// <c>ElementName</c> or an explicit <c>Source</c> (which is what keeps <c>loc:Str</c> out of scope).
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class DataTemplateHostedViewParseTests
{
    /// <summary>
    /// The code-assigned re-root the markup cannot express: <c>TodoPanelControl</c>'s ctor NULLS its
    /// <c>DataContext</c> to break inheritance from the hosting view, and its <c>Loaded</c> handler assigns a
    /// <see cref="TodoViewModel"/> from the window's scoped provider. Without this map, walking the top-level
    /// <c>OptimizeView</c> reports the panel's eight paths as <c>UNRESOLVED</c> against
    /// <see cref="OptimizeViewModel"/> — which is neither a defect nor a truth. Every half of that claim is
    /// pinned by <see cref="TodoPanelControlParseTests"/>; this map is only sound because those facts are.
    /// </summary>
    internal static readonly Dictionary<Type, Type> CodeAssignedRoots =
        new() { [typeof(Pia.Views.TodoPanelControl)] = typeof(TodoViewModel) };

    /// <summary>
    /// Floors, not counts (measured 2026-08-02: 22 / 28 / 29 / 30 / 13 / 218 / 15). Set well under the
    /// measurement so ordinary markup edits never touch this file, while a genuine collapse — a container
    /// that stops reporting logical children — is still caught long before the floor is reached.
    /// <para>
    /// <c>Pia.Views.OptimizeView</c> is the TOP-LEVEL one. <c>Pia.Views.SettingsViews.OptimizeView</c> is a
    /// different type with the same file name, already covered by <see cref="OptimizeViewParseTests"/>; both
    /// exist and both compile, so the fully-qualified name here is load-bearing.
    /// </para>
    /// </summary>
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
            // The host mapping, EXECUTED rather than reflected. A missing key is a null template and a
            // NullReferenceException here would say nothing useful, so it is turned into a named failure.
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
        // The two-halves assertion the re-root needs, in the shape GeneralViewParseTests uses for its markup
        // re-root. Asserting only the re-root LINE would pass if the walk stopped following it; asserting only
        // a [TodoViewModel] path would pass if some other element supplied that context. Both, or neither.
        //
        // This is also the fact that makes the theory above honest for OptimizeView: without the re-root those
        // eight paths read UNRESOLVED, and the tempting "fix" — dropping the panel's subtree from the walk —
        // would have silently removed real coverage instead of rooting it correctly.
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
