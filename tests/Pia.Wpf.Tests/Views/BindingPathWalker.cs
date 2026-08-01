using System.Reflection;
using System.Windows;
using System.Windows.Data;

namespace Pia.Tests.Views;

/// <summary>
/// The binding-path walker shared by every view-parse test that checks a declared <c>Binding</c> path
/// against the ViewModel surface it is rooted at, without ever constructing that ViewModel or evaluating
/// the binding. Five members lifted verbatim (Batch 14 G1) from <c>SettingsAssistantViewParseTests</c> —
/// <see cref="Walk"/>, <see cref="BoundPath"/>, <see cref="TargetsDataContext"/>,
/// <see cref="ResolvePathType"/>, <see cref="FindLogical{T}"/> — with only their visibility changed
/// (<c>private</c> → <c>internal</c>); <see cref="Describe"/> is new here (D7), moving an existing
/// projection expression into the one place its format string can be owned once.
/// </summary>
internal static class BindingPathWalker
{
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
    internal static IEnumerable<(string Element, string Property, string Path, string ContextType, bool Resolves)>
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
    internal static string? BoundPath(DependencyObject element, DependencyProperty property)
    {
        if (element.ReadLocalValue(property) is not BindingExpression expression) return null;
        if (!TargetsDataContext(expression.ParentBinding)) return null;
        var path = expression.ParentBinding.Path?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    internal static bool TargetsDataContext(Binding binding) =>
        binding.RelativeSource is null && binding.ElementName is null && binding.Source is null;

    /// <summary>
    /// Walks a dotted binding path across public instance properties and returns the type it lands on, or
    /// null at the first segment that does not exist — which is the typo this file is for. An indexer is
    /// truncated to the property carrying it (<c>Items[0]</c> → <c>Items</c>), because a path alone does not
    /// say what the element type is.
    /// </summary>
    internal static Type? ResolvePathType(Type root, string path)
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
    internal static IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit)
            yield return hit;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in FindLogical<T>(child))
                yield return descendant;
    }

    /// <summary>
    /// <see cref="Walk"/>, projected into the one string format every caller's <c>Assert.Contains</c>
    /// anchors on: <c>{Element}.{Property}={Path} [{ContextType}] {ok|UNRESOLVED}</c>. Moved here so the
    /// format string exists in exactly one place — a drifted copy would silently break the anchors in
    /// whichever file drifted.
    /// </summary>
    internal static string[] Describe(DependencyObject root, Type? contextType) =>
        Walk(root, contextType)
            .Select(b => $"{b.Element}.{b.Property}={b.Path} [{b.ContextType}] {(b.Resolves ? "ok" : "UNRESOLVED")}")
            .ToArray();
}
