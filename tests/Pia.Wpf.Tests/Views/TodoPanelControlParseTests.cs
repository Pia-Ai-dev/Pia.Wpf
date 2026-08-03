using System.Windows;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>TodoPanelControl</c> — and it inverts the host guard every other file in this folder applies.
/// <para>
/// Both of its host sites (<c>AssistantView.xaml:521</c>, <c>OptimizeView.xaml:483</c>) declare <b>no</b>
/// <c>DataContext</c> binding, so by markup alone the panel would inherit the hosting view's ViewModel —
/// <see cref="AssistantViewModel"/> at one site and <see cref="OptimizeViewModel"/> at the other, neither of
/// which carries a single one of its paths. It does not, because its CTOR sets <c>DataContext = null</c>
/// specifically to break that inheritance, and its <c>Loaded</c> handler assigns a <see cref="TodoViewModel"/>
/// from the window's scoped provider.
/// </para>
/// <para>
/// <b>So the correctness condition here is the opposite of the usual one, and it needs all three halves.</b>
/// If a future edit deletes the ctor line, the panel silently inherits the host's ViewModel at both sites and
/// every path in it mis-binds, with the build at zero warnings and — before this file — the whole suite
/// green. The third fact is the only one that reds for that, and it is the one that looks trivial.
/// </para>
/// <para>
/// These three facts are also what makes
/// <see cref="DataTemplateHostedViewParseTests.CodeAssignedRoots"/> sound: that map tells the walker to
/// re-root this panel's subtree at <see cref="TodoViewModel"/> when it is reached through the top-level
/// <c>OptimizeView</c>, which is only true while all three hold. Break any of them and the map becomes a
/// fiction the walker would happily keep asserting.
/// </para>
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
        // <b>Read the shape of this fact, because the obvious version of it is VACUOUS and was written first.</b>
        // Asserting `new TodoPanelControl().DataContext is null` passes whether or not the ctor line exists: an
        // unparented control has a null DataContext by default, so the assertion observes the default and not
        // the mechanism. Demonstrated, not reasoned — commenting out `DataContext = null;` left that version
        // GREEN, which is the exact failure mode this whole line of work exists to prevent.
        //
        // The mechanism only shows itself against a host that HAS a DataContext, because what the ctor line
        // does is write a local null that blocks INHERITANCE. So: parse the real host, give it a sentinel, and
        // read the panel. Neutralization: comment out `DataContext = null;` → the panel inherits the sentinel
        // and this reds.
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

    /// <summary>
    /// The <c>DataContext</c> path declared at the single <c>TodoPanelControl</c> site inside one host view,
    /// found by TYPE on the parsed logical tree — never by index, because logical-walk order is a measured
    /// property of the markup and not something to assert against. Folds the count into the returned string
    /// so "not found", "found twice" and "wrong path" all produce one readable message.
    /// </summary>
    private static string PathAt<THost>() where THost : FrameworkElement, new()
    {
        var panels = BindingPathWalker.FindLogical<Pia.Views.TodoPanelControl>(new THost()).ToArray();
        if (panels.Length != 1)
            return $"<{panels.Length} TodoPanelControl(s) in the logical tree, expected exactly 1>";

        return BindingPathWalker.BoundPath(panels[0], FrameworkElement.DataContextProperty) ?? "<none>";
    }
}
