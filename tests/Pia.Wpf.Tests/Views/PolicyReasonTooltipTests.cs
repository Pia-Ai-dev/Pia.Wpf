using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A control greyed out by enterprise policy has to say why, and WPF suppresses tooltips on a disabled
/// control unless <see cref="ToolTipService.ShowOnDisabledProperty"/> is set — which the views set once on
/// their root and inherit. Both halves are asserted here: the inheritance (so the single root setting is
/// evidence, not assumption) and one reason binding per policy-locked control.
/// </summary>
[Collection("WpfApplicationStatic")]
public class PolicyReasonTooltipTests
{
    private const int MaxTemplateDepth = 8;

    /// <summary>
    /// The icon-only Delete-voice button: its tooltip IS its label, and the reason binding is null whenever
    /// policy is not enforcing — which for this control is the normal case — so it would trade a label every
    /// user needs for a sentence a managed few see.
    /// </summary>
    private static readonly string[] Exempt = ["DataContext.Policy[TtsVoiceModelKey]"];

    private sealed record Locked(string Path, string? ToolTipPath, bool ShowOnDisabled, bool InTemplate);

    [Theory]
    [InlineData(typeof(Pia.Views.SettingsViews.GeneralView), 9)]
    [InlineData(typeof(Pia.Views.SettingsViews.AssistantView), 20)]
    [InlineData(typeof(Pia.Views.SettingsViews.AccountView), 1)]
    [InlineData(typeof(Pia.Views.SettingsViews.OptimizeView), 2)]
    [InlineData(typeof(Pia.Views.SettingsViews.ProvidersView), 1)]
    [InlineData(typeof(Pia.Views.SettingsViews.TemplatesView), 1)]
    public void EveryPolicyLockedControl_ExplainsItselfOnHover(Type viewType, int minimumLocked)
    {
        var locked = WpfStaHost.Run(() => Survey(viewType));

        Assert.True(locked.Length >= minimumLocked,
            $"only {locked.Length} policy-locked controls were found in {viewType.Name}, below the " +
            $"non-vacuity floor of {minimumLocked} — suspect the IsEnabled binding shape, not a removal.");

        var silent = locked
            .Where(l => !Exempt.Contains(l.Path))
            .Where(l => l.ToolTipPath is null || !l.ToolTipPath.Contains("Policy.Reason[", StringComparison.Ordinal))
            .Select(l => $"{l.Path} (tooltip: {l.ToolTipPath ?? "none"})")
            .ToArray();

        Assert.True(silent.Length == 0,
            $"these controls in {viewType.Name} are disabled by policy without saying why. Add " +
            "ToolTip=\"{Binding Policy.Reason[<SettingName>]}\" beside the IsEnabled binding: " +
            string.Join("; ", silent));

        // Only the controls the real tree holds: DataTemplate content is loaded detached, where nothing
        // inherits.
        var notInherited = locked
            .Where(l => !l.InTemplate && !l.ShowOnDisabled)
            .Select(l => l.Path)
            .ToArray();

        Assert.True(notInherited.Length == 0,
            $"ToolTipService.ShowOnDisabled did not reach these controls in {viewType.Name}, so their reason " +
            "tooltip stays invisible in the one state it exists for. Set it on the view root: " +
            string.Join("; ", notInherited));
    }

    private static Locked[] Survey(Type viewType)
    {
        var root = (FrameworkElement)Activator.CreateInstance(viewType)!;
        var found = new List<Locked>();
        Collect(root, root, found, [], 0, false);
        return [.. found];
    }

    private static void Collect(DependencyObject element, DependencyObject root, List<Locked> found,
        HashSet<DataTemplate> open, int depth, bool inTemplate)
    {
        if (depth > MaxTemplateDepth) return;
        if (!ReferenceEquals(element, root) && element is UserControl) return;

        if (BoundPath(element, UIElement.IsEnabledProperty) is { } path && IsPolicyPath(path))
        {
            found.Add(new Locked(
                path,
                BoundPath(element, FrameworkElement.ToolTipProperty),
                ToolTipService.GetShowOnDisabled(element),
                inTemplate));
        }

        foreach (var property in (DependencyProperty[])
                 [ItemsControl.ItemTemplateProperty, ContentControl.ContentTemplateProperty])
        {
            if (element.ReadLocalValue(property) is not DataTemplate template || !open.Add(template)) continue;
            if (template.LoadContent() is DependencyObject content)
                Collect(content, content, found, open, depth + 1, true);
            open.Remove(template);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            Collect(child, root, found, open, depth, inTemplate);
    }

    // Both shapes in the markup: the PolicyLock indexer, and the older per-setting Is…Enforced property.
    private static bool IsPolicyPath(string path) =>
        path.Contains("Policy[", StringComparison.Ordinal)
        || (path.StartsWith("Is", StringComparison.Ordinal)
            && path.EndsWith("Enforced", StringComparison.Ordinal));

    private static string? BoundPath(DependencyObject element, DependencyProperty property) =>
        element.ReadLocalValue(property) is BindingExpressionBase expression
            ? (expression.ParentBindingBase as Binding)?.Path?.Path
            : null;
}
