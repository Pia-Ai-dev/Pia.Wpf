using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Nothing in <c>dotnet test</c> replays the recorded UI scripts, so an id dropped in a XAML edit stays
/// invisible until a walkthrough breaks. Presence only — several ids predate the naming convention.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ViewAutomationIdTests
{
    /// <summary>Bounds template recursion; the deepest real nesting here is ToolCatalog's group-then-row pair.</summary>
    private const int MaxTemplateDepth = 8;

    // DataTemplates only, and only ones the markup sets locally: expanding ControlTemplates too would drag in
    // Wpf.Ui's ScrollBar arrows and ComboBox toggles and bury the signal.
    private static readonly DependencyProperty[] DeclaredTemplates =
    [
        ItemsControl.ItemTemplateProperty,
        ContentControl.ContentTemplateProperty,
        HeaderedContentControl.HeaderTemplateProperty,
    ];

    /// <summary>Most-specific first: the property a script would drive is the one that names the control.</summary>
    private static readonly string[] PrimaryProperties =
        ["IsChecked", "Text", "Value", "SelectedItem", "SelectedValue", "Password", "ItemsSource", "Command"];

    private enum IdKind { Missing, Empty, Literal, PerItem }

    private sealed record Inspected(string Type, string Identity, IdKind Id, bool InItemTemplate);

    private sealed record Survey(Inspected[] Controls, string[] NestedViews);

    // The walk stops at every nested UserControl, which owns its own ids, and each case pins WHICH ones — so a
    // part of a covered view later extracted into its own UserControl cannot drop out of coverage silently.
    // The playbook's "Known gaps" section is the single source of truth for what still has no row here.
    [Theory]
    [InlineData(typeof(Pia.Views.SettingsViews.GeneralView), 24, 4, "")]
    [InlineData(typeof(Pia.Views.SettingsViews.AssistantView), 36, 5, "PersonaGlyph,PersonasView,PiaHelpHint")]
    [InlineData(typeof(Pia.Views.SettingsViews.ProvidersView), 6, 3, "")]
    // AccountView declares no DataTemplate, so it is the one view with no per-item floor to hold.
    [InlineData(typeof(Pia.Views.SettingsViews.AccountView), 12, 0, "E2EEOnboardingView")]
    [InlineData(typeof(Pia.Views.SettingsViews.OptimizeView), 6, 4, "")]
    [InlineData(typeof(Pia.Views.AssistantView), 18, 1,
        "AutocompletePopup,DirectTranscriptionOverlay,MeetingAttendeeOverlay,PersonaGlyph,PiaAssistantMessage," +
        "PiaChatQuickSwitcher,PiaChatTitleChip,PiaPersonaAvatar,RunProgressPanel,TodoPanelControl,VoiceModeOverlay")]
    [InlineData(typeof(Pia.Views.RoutinesView), 15, 1, "PiaEmptyState,PiaHelpHint")]
    [InlineData(typeof(Pia.Views.SettingsViews.PersonasView), 3, 3, "PersonaGlyph")]
    [InlineData(typeof(Pia.Views.MeetingAttendeeOverlay), 8, 1, "ListeningIndicator")]
    [InlineData(typeof(Pia.Controls.Cards.CardDecisionBar), 1, 1, "")]
    [InlineData(typeof(Pia.Controls.Vault.PiaVaultHeader), 5, 0, "PiaHelpHint")]
    [InlineData(typeof(Pia.Controls.Vault.PiaVaultSearchBar), 1, 0, "")]
    [InlineData(typeof(Pia.Controls.Reminders.PiaRemindersHeader), 4, 0, "PiaHelpHint")]
    [InlineData(typeof(Pia.Controls.Reminders.PiaRemindersFilterBar), 5, 0, "")]
    [InlineData(typeof(Pia.Controls.History.PiaHistoryHeader), 2, 0, "PiaHelpHint")]
    [InlineData(typeof(Pia.Controls.History.PiaHistorySearchBar), 3, 0, "")]
    [InlineData(typeof(Pia.Controls.Todo.PiaTodoHeader), 2, 0, "PiaHelpHint")]
    [InlineData(typeof(Pia.Controls.Todo.PiaTodoSearchBar), 1, 0, "")]
    [InlineData(typeof(Pia.Controls.Markdown.CodeBlockControl), 2, 0, "")]
    [InlineData(typeof(Pia.Controls.Chat.PiaAnswerToolbar), 7, 7, "")]
    [InlineData(typeof(Pia.Controls.Vault.PiaVaultCategoryCard), 1, 1, "PiaTypeChip")]
    [InlineData(typeof(Pia.Controls.Reminders.PiaReminderRow), 4, 4, "PiaReminderStatusChip")]
    [InlineData(typeof(Pia.Controls.Reminders.PiaReminderGroupCard), 5, 5, "PiaReminderStatusChip")]
    [InlineData(typeof(Pia.Controls.History.PiaHistoryGroupCard), 1, 1, "")]
    [InlineData(typeof(Pia.Controls.AssistantHistory.PiaAssistantChatRowContent), 1, 1, "PiaChatStateBadge")]
    [InlineData(typeof(Pia.Controls.AssistantHistory.PiaAssistantChatGroupCard), 1, 1, "PiaAssistantChatRowContent")]
    [InlineData(typeof(Pia.Views.TodoView), 9, 5, "PiaTodoHeader,PiaTodoSearchBar")]
    [InlineData(typeof(Pia.Views.TodoPanelControl), 6, 1, "")]
    [InlineData(typeof(Pia.Controls.Assistant.PiaChatQuickSwitcher), 1, 0, "")]
    [InlineData(typeof(Pia.Controls.Chat.PiaReasoningView), 1, 1, "")]
    [InlineData(typeof(Pia.Controls.Assistant.RunProgressPanel), 21, 10, "PiaPersonaAvatar")]
    [InlineData(typeof(Pia.Controls.Chat.PiaFileChip), 3, 3, "")]
    [InlineData(typeof(Pia.Controls.Chat.PiaSourceChip), 1, 1, "")]
    [InlineData(typeof(Pia.Controls.Chat.PiaChipOverflowPanel), 1, 1, "")]
    [InlineData(typeof(Pia.Controls.ActionCardControl), 3, 3, "CardDecisionBar,FileDiffCard")]
    [InlineData(typeof(Pia.Controls.Cards.FileDiffCard), 1, 1, "")]
    [InlineData(typeof(Pia.Controls.Flow.FlowView), 10, 10, "CardDecisionBar,PiaChatStateBadge")]
    public void EveryInteractiveControl_CarriesAnAutomationId(
        Type viewType, int minimumInspected, int minimumPerItemIds, string expectedNestedViews)
    {
        var survey = WpfStaHost.Run(() => Take(viewType));

        var missing = survey.Controls
            .Where(c => c.Id is IdKind.Missing or IdKind.Empty)
            .Select(c => $"{c.Type} ({c.Identity}){(c.Id == IdKind.Empty ? " - its id is an empty literal" : "")}")
            .ToArray();

        Assert.True(missing.Length == 0,
            $"these interactive controls in {viewType.Name} carry no AutomationId, so a recorded UI script can " +
            "only reach them through their localized Content/Name and breaks in any other UI language. Add " +
            "AutomationProperties.AutomationId=\"<ViewPrefix>_<Field>\", or the per-item binding form " +
            $"\"{{Binding <Identity>, StringFormat='<ViewPrefix>_<Field>_{{0}}'}}\" inside a DataTemplate: " +
            $"{string.Join("; ", missing)}");

        // A floor, not a count, set well under the measured total so ordinary edits to the view never touch this file.
        Assert.True(survey.Controls.Length >= minimumInspected,
            $"only {survey.Controls.Length} interactive controls were inspected in {viewType.Name}, below the " +
            $"non-vacuity floor of {minimumInspected}. The walk is logical, so suspect a container that no " +
            "longer reports logical children, or a predicate arm that stopped matching, rather than a genuine " +
            "removal.");

        // An unexpanded template reports nothing missing, so the assertion above cannot fail on it. A per-item
        // id is a Binding, which only exists if the template really was expanded.
        var perItem = survey.Controls.Count(c => c.Id == IdKind.PerItem);
        Assert.True(perItem >= minimumPerItemIds,
            $"only {perItem} of {viewType.Name}'s AutomationIds are the per-item binding form, below the " +
            $"expected {minimumPerItemIds}. Either a DataTemplate is no longer being expanded - which would " +
            "make the missing-id assertion above vacuous for every control inside it - or a per-item id was " +
            "replaced by a literal.");

        var sharedRowIds = survey.Controls
            .Where(c => c.InItemTemplate && c.Id == IdKind.Literal)
            .Select(c => $"{c.Type} ({c.Identity})")
            .ToArray();

        Assert.True(sharedRowIds.Length == 0,
            $"these controls sit inside an ItemTemplate in {viewType.Name} and carry a LITERAL AutomationId, so " +
            "every row reports the same id and ww_invoke silently takes the first — the exact ambiguity an id is " +
            $"supposed to remove. Use \"{{Binding <Identity>, StringFormat='<Prefix>_{{0}}'}}\": " +
            $"{string.Join("; ", sharedRowIds)}");

        Assert.Equal(
            expectedNestedViews.Length == 0 ? [] : expectedNestedViews.Split(','),
            survey.NestedViews);
    }

    private static Survey Take(Type viewType)
    {
        var root = (FrameworkElement)Activator.CreateInstance(viewType)!;
        var controls = new List<Inspected>();
        var nested = new List<string>();
        Collect(root, root, controls, nested, [], 0, false);
        return new Survey([.. controls], [.. nested.Distinct().OrderBy(n => n, StringComparer.Ordinal)]);
    }

    private static void Collect(DependencyObject element, DependencyObject root, List<Inspected> controls,
        List<string> nested, HashSet<DataTemplate> open, int depth, bool inItemTemplate)
    {
        if (depth > MaxTemplateDepth) return;

        // A nested view owns its own ids and its own guard; descending would report its controls against this file.
        if (!ReferenceEquals(element, root) && element is UserControl)
        {
            nested.Add(element.GetType().Name);
            return;
        }

        // Expander and TabItem are here because a script has to expand or select them to reach anything inside,
        // which without an id means matching their localized header.
        if (element is ButtonBase or ComboBox or TextBoxBase or PasswordBox or Slider or Expander or TabItem)
            controls.Add(new Inspected(element.GetType().FullName!, Identity(element), Id(element), inItemTemplate));

        // ReadLocalValue, so a template inherited from a default Wpf.Ui style is not expanded.
        foreach (var property in DeclaredTemplates)
        {
            if (element.ReadLocalValue(property) is not DataTemplate template) continue;
            if (!open.Add(template)) continue;
            if (template.LoadContent() is DependencyObject content)
                Collect(content, content, controls, nested, open, depth + 1,
                    inItemTemplate || property == ItemsControl.ItemTemplateProperty);
            open.Remove(template);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            Collect(child, root, controls, nested, open, depth, inItemTemplate);
    }

    /// <summary>A per-item id is a Binding that evaluates to "" without an item, so the LOCAL VALUE is read -
    /// GetAutomationId would report every templated control as missing.</summary>
    private static IdKind Id(DependencyObject element) =>
        element.ReadLocalValue(AutomationProperties.AutomationIdProperty) switch
        {
            BindingExpressionBase => IdKind.PerItem,
            string text when string.IsNullOrWhiteSpace(text) => IdKind.Empty,
            string => IdKind.Literal,
            var value when value == DependencyProperty.UnsetValue => IdKind.Missing,
            _ => IdKind.Literal,
        };

    /// <summary>Never Content/Text — those are loc:Str values. Read from local values rather than named
    /// DependencyProperties, so ui:NumberBox's own Value is seen too.</summary>
    private static string Identity(DependencyObject element)
    {
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = element.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            if (values.Current.Value is not BindingExpressionBase expression) continue;
            var path = (expression.ParentBindingBase as Binding)?.Path?.Path;
            if (!string.IsNullOrWhiteSpace(path)) bound[values.Current.Property.Name] = path;
        }

        foreach (var name in PrimaryProperties)
            if (bound.TryGetValue(name, out var path))
                return $"{name}={path}";

        // AccountView's PasswordBox is pushed from code-behind, so x:Name is its only stable identity.
        if (element is FrameworkElement { Name.Length: > 0 } named) return $"x:Name={named.Name}";
        return "<nothing bound>";
    }
}
