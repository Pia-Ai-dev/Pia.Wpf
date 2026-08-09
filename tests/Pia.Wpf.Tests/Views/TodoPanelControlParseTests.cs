using System.Windows;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Neither host site binds <c>DataContext</c>: the panel's ctor nulls it to block inheritance and <c>Loaded</c>
/// assigns a <see cref="TodoViewModel"/>, which is what <c>CodeAssignedRoots</c>' re-root map rests on.
/// </summary>
[Collection("WpfApplicationStatic")]
public class TodoPanelControlParseTests
{
    /// <summary>A floor, not a count: measured 8 on 2026-08-02.</summary>
    private const int MinimumBoundPaths = 5;

    [Fact]
    public void EveryBindingPath_ResolvesOnTodoViewModel()
    {
        // No re-root map is passed: the walk is ALREADY rooted at TodoViewModel here, and passing the map
        // would add its own re-root line for the root element and inflate the count by one for nothing.
        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.TodoPanelControl(), typeof(TodoViewModel)));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed TodoPanelControl, which is below " +
            $"the non-vacuity floor of {MinimumBoundPaths}. The walk is LOGICAL, so suspect a container that " +
            "no longer reports logical children rather than a genuine removal.");

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/TodoPanelControl.xaml do not resolve to a public property on " +
            $"TodoViewModel, so they bind to nothing and fail silently at runtime: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void NeitherHostSite_BindsTheDataContext_BecauseTheCodeOwnsIt()
    {
        var observed = WpfStaHost.Run(() => new[]
        {
            $"AssistantView={PathAt<Pia.Views.AssistantView>()}",
            $"OptimizeView={PathAt<Pia.Views.OptimizeView>()}",
        });

        // A host site that GAINED a DataContext binding would fight the ctor's null and the Loaded assignment
        // — last writer wins, and which one that is depends on load order. Reds here rather than at runtime.
        Assert.Equal(["AssistantView=<none>", "OptimizeView=<none>"], observed);
    }

    [Fact]
    public void ThePanel_DoesNotInheritItsHostsDataContext_BecauseTheCtorNullsIt()
    {
        // Asserting `new TodoPanelControl().DataContext is null` is vacuous — an unparented control reads null
        // anyway. Only a host that HAS a DataContext shows the ctor's local null blocking inheritance.
        var sentinel = new object();
        var inherited = WpfStaHost.Run(() =>
        {
            var assistant = new Pia.Views.AssistantView { DataContext = sentinel };
            var panel = BindingPathWalker.FindLogical<Pia.Views.TodoPanelControl>(assistant).Single();
            return ReferenceEquals(panel.DataContext, sentinel);
        });

        Assert.False(inherited,
            "TodoPanelControl inherited its host's DataContext. Its ctor sets DataContext = null precisely to " +
            "stop that, because its own paths live on TodoViewModel and none of them exists on the hosting " +
            "AssistantViewModel or OptimizeViewModel — so every binding in the panel would be dead at runtime " +
            "with the build at zero warnings. This also invalidates " +
            $"{nameof(DataTemplateHostedViewParseTests)}.{nameof(DataTemplateHostedViewParseTests.CodeAssignedRoots)}, " +
            "which re-roots this panel's subtree at TodoViewModel on the strength of that ctor line.");
    }

    // Found by TYPE, never by index: logical-walk order is a property of the markup, not something to assert on.
    private static string PathAt<THost>() where THost : FrameworkElement, new()
    {
        var panels = BindingPathWalker.FindLogical<Pia.Views.TodoPanelControl>(new THost()).ToArray();
        if (panels.Length != 1)
            return $"<{panels.Length} TodoPanelControl(s) in the logical tree, expected exactly 1>";

        return BindingPathWalker.BoundPath(panels[0], FrameworkElement.DataContextProperty) ?? "<none>";
    }
}
