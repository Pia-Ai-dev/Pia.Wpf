using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A control greyed out by enterprise policy has to say why, and WPF suppresses tooltips on a disabled
/// control unless <see cref="ToolTipService.ShowOnDisabledProperty"/> is set. Both halves are asserted per
/// locked control: the reason binding, and that flag — which is set on each control because setting it on
/// the view root did not reach them.
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

    private sealed record Locked(string Path, string? ToolTipPath, bool ShowOnDisabled);

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

        var hidden = locked
            .Where(l => !l.ShowOnDisabled)
            .Select(l => l.Path)
            .ToArray();

        Assert.True(hidden.Length == 0,
            $"these controls in {viewType.Name} carry no ToolTipService.ShowOnDisabled, so their tooltip " +
            "stays invisible in the one state it exists for. Add ToolTipService.ShowOnDisabled=\"True\" to " +
            "the control itself — on the view root it does not reach them: " + string.Join("; ", hidden));
    }

    /// <summary>
    /// The reason is a full sentence in a 260px-wide tooltip, so it wraps — and WPF-UI's ToolTip style
    /// justifies it, which stretches the wrapped lines into gappy columns. The value is inherited from the
    /// tooltip rather than set on the TextBlock, which is why the override is a derived ToolTip style.
    /// </summary>
    [Fact]
    public void TheToolTipContainer_AlignsItsTextLeft()
    {
        var alignment = WpfStaHost.Run(() =>
        {
            var tip = new ToolTip { Content = "Locked by your organization: this setting is enforced." };
            tip.ApplyTemplate();
            tip.Measure(new Size(300, 300));
            return (TextAlignment)tip.GetValue(TextBlock.TextAlignmentProperty);
        });

        Assert.Equal(TextAlignment.Left, alignment);
    }

    private static Locked[] Survey(Type viewType)
    {
        var root = (FrameworkElement)Activator.CreateInstance(viewType)!;
        var found = new List<Locked>();
        Collect(root, root, found, [], 0);
        return [.. found];
    }

    private static void Collect(DependencyObject element, DependencyObject root, List<Locked> found,
        HashSet<DataTemplate> open, int depth)
    {
        if (depth > MaxTemplateDepth) return;
        if (!ReferenceEquals(element, root) && element is UserControl) return;

        if (BoundPath(element, UIElement.IsEnabledProperty) is { } path && IsPolicyPath(path))
        {
            found.Add(new Locked(
                path,
                BoundPath(element, FrameworkElement.ToolTipProperty),
                ToolTipService.GetShowOnDisabled(element)));
        }

        foreach (var property in (DependencyProperty[])
                 [ItemsControl.ItemTemplateProperty, ContentControl.ContentTemplateProperty])
        {
            if (element.ReadLocalValue(property) is not DataTemplate template || !open.Add(template)) continue;
            if (template.LoadContent() is DependencyObject content)
                Collect(content, content, found, open, depth + 1);
            open.Remove(template);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            Collect(child, root, found, open, depth);
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
