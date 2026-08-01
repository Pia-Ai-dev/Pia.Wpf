using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The SETTINGS Assistant view — <c>Pia.Views.SettingsViews.AssistantView</c>, which shares nothing but a
/// file name with <c>Pia.Views.AssistantView</c> (the chat view <see cref="AssistantViewParseTests"/>
/// parses). Three batches booked this file as manual-smoke debt for the same reason, and it was always the
/// same reason: <b>a misspelled <c>Binding</c> path renders a control that silently never persists</b> —
/// Batch 04's smoke item 1 (the auto-approve CheckBox), Batch 05's (the plan-reasoning CheckBox, whose debt
/// entry says in so many words that no test parses this view) and Phase 3's R11 (the persona roster
/// surface). Nothing could be written until <see cref="WpfStaHost"/> tolerated another frame-driving fact.
/// <para>
/// <b>What this does NOT prove, stated because the roadmap items it shortens are worded as round trips:</b>
/// those items say "toggle it, restart, confirm it stuck". This proves the path resolves and the string
/// renders — the half that fails SILENTLY and that no other test can see. It says nothing about whether a
/// value reaches disk, so the persistence half of each item stays on the manual round.
/// </para>
/// <para>
/// No ViewModel is constructed, deliberately: <c>AssistantSettingsViewModel</c> takes four concrete
/// sub-ViewModels, and that cost is what made every previous attempt disproportionate. A declared binding's
/// PATH is readable without ever evaluating it, so the check is the parsed path against the reflected
/// ViewModel surface — cheaper than an instance, and it fails for exactly the reason a typo fails.
/// </para>
/// <para>
/// This view is NOT one DataContext, which the first version of this file got wrong and the failure taught:
/// three sections re-root to sub-ViewModels (<c>PersonasVm</c> at :163, <c>ToolPermissionsVm</c> at :168,
/// <c>MeetingVm</c> at :249) and one of them is a nested <c>UserControl</c> with its own bindings. So the
/// walk carries the effective DataContext TYPE down the tree and re-roots wherever the markup does. That
/// makes this stronger than the single-type check it replaced: it covers the composed page, sub-views
/// included, and it would catch a section re-rooted at the wrong sub-ViewModel.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class SettingsAssistantViewParseTests
{
    /// <summary>
    /// A floor, not a count: "no unresolved paths" is vacuously true over an empty walk, which is reachable
    /// (a container swapped for a templated one stops a logical walk dead). Deliberately well under the real
    /// number so ordinary edits to the view never touch this file.
    /// </summary>
    private const int MinimumBoundPaths = 20;

    /// <summary>
    /// ViewStrings.resx (neutral = EN), rendered by this view through <c>TextBlock.Text</c> at
    /// <c>AssistantView.xaml:443</c>. No test calls SetCulture, so the neutral resx is deterministically what
    /// the view renders — the same reasoning <see cref="AssistantViewParseTests"/> records for its own anchor.
    /// </summary>
    private const string RosterHeaderText = "Step specialists";

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // The root DataContext is CHECKED, not assumed: SettingsView.xaml:110 hosts this view with
        // DataContext="{Binding AssistantVm}", so the walk below is only sound while that property still has
        // this type. Reading it by reflection means a future re-host fails here instead of quietly making
        // every path in this file resolve against the wrong type.
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.AssistantVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(AssistantSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            Walk(new Pia.Views.SettingsViews.AssistantView(), root)
                .Select(b => $"{b.Element}.{b.Property}={b.Path} [{b.ContextType}] {(b.Resolves ? "ok" : "UNRESOLVED")}")
                .ToArray());

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed settings AssistantView, which is " +
            $"below the non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container " +
            "that no longer reports logical children rather than a genuine removal.");

        // The three paths that are the whole reason this file exists, named so that renaming one along with
        // its XAML still leaves a test mentioning the smoke item it came from.
        Assert.Contains(bindings, b => b.Contains("=AgentPlanReasoningTurnEnabled "));     // Batch 05
        Assert.Contains(bindings, b => b.Contains("=AgentRunAutoApproveBuiltInWrites "));  // Batch 04
        Assert.Contains(bindings, b => b.Contains("=AgentRosterOptions "));                // Batch 07 G7 / R11

        // Batch 09's section, and BOTH halves matter. The first is the re-root itself; the second proves the
        // walk followed it, because a path only resolves against ScheduledJobsSettingsViewModel if the
        // DataContext binding above it was understood. Without the second, a section that silently stopped
        // being walked would still satisfy the first.
        Assert.Contains(bindings, b => b.Contains("=ScheduledJobsVm "));
        Assert.Contains(bindings, b => b.Contains("=EditQuery [ScheduledJobsSettingsViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/AssistantView.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void ParsedView_HasNoUnresolvedLocalizationKeys()
    {
        // LocalizationSource returns the literal "[Key]" for an unknown key, and loc:Str binds it with an
        // explicit Source, so it resolves with no DataContext at all. Same scope limit as the chat view's
        // sweep: only loc:Str bound to TextBlock.Text is visible to a logical walk — Content= on a CheckBox
        // or ui:Button becomes a TextBlock only after template application, which this file must not
        // trigger. So this covers section headers and descriptions, not the toggle labels; LocalizationTests
        // covers all of them for key PARITY, and the fact above covers the toggles' binding paths.
        Pia.Views.SettingsViews.AssistantView? view = null;
        WpfStaHost.Run(() =>
        {
            view = new Pia.Views.SettingsViews.AssistantView();
            return 0;
        });
        WpfStaHost.Pump();

        var rendered = WpfStaHost.Run(() =>
            FindLogical<TextBlock>(view!).Select(tb => tb.Text).Where(t => t is not null).ToArray());

        // NON-VACUITY FLOOR, and it carries the whole assertion below — the same guard the chat view's sweep
        // documents, for a sharper reason here: an unbound TextBlock.Text is "" and not null, so a count-only
        // floor would survive a Pump() that stopped draining and sweep a page of empty strings clean, forever.
        // Anchoring on one string this view is known to render makes the DRAIN part of what is under test.
        Assert.Contains(RosterHeaderText, rendered);

        Assert.True(rendered.Length > 0,
            "the logical walk over the parsed settings AssistantView found no TextBlock at all, so the " +
            "sweep below would pass over nothing.");

        var unresolved = rendered.Where(t => Regex.IsMatch(t, @"^\[\w+\]$")).Distinct().ToArray();
        Assert.True(unresolved.Length == 0,
            $"unresolved loc:Str keys among the {rendered.Length} TextBlocks walked in the parsed settings " +
            $"AssistantView: {string.Join(", ", unresolved)}");
    }

    /// <summary>
    /// Every binding in the non-templated logical tree, paired with the ViewModel type the markup roots it
    /// at. <paramref name="contextType"/> is null once an ancestor's own DataContext path could not be
    /// resolved — descendants are then reported as unknown rather than as failures, so one bad re-root
    /// produces one finding instead of a cascade.
    /// <para>
    /// Bindings carrying <c>RelativeSource</c>, <c>ElementName</c> or an explicit <c>Source</c> are skipped
    /// because they do not target the DataContext at all — that filter is what keeps <c>loc:Str</c>
    /// (explicit Source) and the ItemsControl-ancestor command binding at :221 out of scope. MultiBinding is
    /// skipped rather than flattened, and the walk is LOGICAL, so a <c>DataTemplate</c>'s content is never
    /// reached: no path here is item-scoped, which is what makes comparing each to one type sound.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Element, string Property, string Path, string ContextType, bool Resolves)>
        Walk(DependencyObject element, Type? contextType)
    {
        var elementName = element.GetType().Name;

        // A local DataContext binding re-roots this whole subtree, so it is resolved FIRST and its result
        // becomes the context for everything below.
        var contextPath = BoundPath(element, FrameworkElement.DataContextProperty);
        if (contextPath is not null)
        {
            var next = contextType is null ? null : ResolvePathType(contextType, contextPath);
            yield return (elementName, "DataContext", contextPath, contextType?.Name ?? "unknown",
                next is not null);
            contextType = next;
        }

        var values = element.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var property = values.Current.Property;
            if (property == FrameworkElement.DataContextProperty) continue;
            if (values.Current.Value is not BindingExpression expression) continue;
            if (!TargetsDataContext(expression.ParentBinding)) continue;

            var path = expression.ParentBinding.Path?.Path;
            if (string.IsNullOrWhiteSpace(path)) continue;

            yield return (elementName, property.Name, path, contextType?.Name ?? "unknown",
                contextType is not null && ResolvePathType(contextType, path) is not null);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var found in Walk(child, contextType))
                yield return found;
    }

    /// <summary>The DataContext-targeting path bound to one property, or null if it is unbound or bound
    /// somewhere other than the DataContext.</summary>
    private static string? BoundPath(DependencyObject element, DependencyProperty property)
    {
        if (element.ReadLocalValue(property) is not BindingExpression expression) return null;
        if (!TargetsDataContext(expression.ParentBinding)) return null;
        var path = expression.ParentBinding.Path?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool TargetsDataContext(Binding binding) =>
        binding.RelativeSource is null && binding.ElementName is null && binding.Source is null;

    /// <summary>
    /// Walks a dotted binding path across public instance properties and returns the type it lands on, or
    /// null at the first segment that does not exist — which is the typo this file is for. An indexer is
    /// truncated to the property carrying it (<c>Items[0]</c> → <c>Items</c>), because a path alone does not
    /// say what the element type is.
    /// </summary>
    private static Type? ResolvePathType(Type root, string path)
    {
        var current = root;
        foreach (var raw in path.Split('.'))
        {
            var name = raw;
            var bracket = name.IndexOf('[');
            if (bracket >= 0) name = name[..bracket];
            if (name.Length == 0) continue;   // an indexer on the DataContext itself, e.g. "[Key]"

            var property = current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null) return null;
            current = property.PropertyType;
        }

        return current;
    }

    /// <summary>Logical, for the reason <see cref="AssistantViewParseTests"/> documents: a UserControl's
    /// Content is not a VISUAL child until its template is applied, and applying it needs layout.</summary>
    private static IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit)
            yield return hit;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in FindLogical<T>(child))
                yield return descendant;
    }
}
