using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
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
        // The root TYPE is checked, not assumed — but ONLY the type: nameof makes a RENAME of
        // SettingsViewModel.AssistantVm a compile error and the Assert.Equal below catches a RETYPE. Nothing
        // here opens SettingsView.xaml, so the host SITE (:110, DataContext="{Binding AssistantVm}") is not
        // observed by this fact; ViewHostDataContextTests is the guard that reads it. The claim this comment
        // used to make — that reading the root by reflection makes a future RE-HOST fail here — was false,
        // and the Batch 14 review disproved it by execution (D1): a re-host leaves every path in this file
        // resolving against a type the markup no longer uses, at 0 warnings and 16/16 green. This walk needs
        // both halves.
        var root = typeof(SettingsViewModel)
            .GetProperty(nameof(SettingsViewModel.AssistantVm), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(AssistantSettingsViewModel), root);

        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(new Pia.Views.SettingsViews.AssistantView(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed settings AssistantView, which is " +
            $"below the non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container " +
            "that no longer reports logical children rather than a genuine removal.");

        // The three paths that are the whole reason this file exists, named so that renaming one along with
        // its XAML still leaves a test mentioning the smoke item it came from.
        Assert.Contains(bindings, b => b.Contains("=AgentPlanReasoningTurnEnabled "));     // Batch 05
        Assert.Contains(bindings, b => b.Contains("=AgentRunAutoApproveBuiltInWrites "));  // Batch 04
        Assert.Contains(bindings, b => b.Contains("=AgentRosterOptions "));                // Batch 07 G7 / R11

        // Batch 14 D5: PersonasView (hosted at :163, re-rooted at PersonasVm) has no standalone parse test —
        // SettingsViewModel.PersonasVm type-matches AssistantSettingsViewModel.PersonasVm by coincidence
        // (both PersonaSettingsViewModel), so reflecting its root off SettingsViewModel proves nothing (W11).
        // This walk already reaches it correctly, under the real host: the re-root itself shows up in the
        // dump as "PersonasView.DataContext=PersonasVm [AssistantSettingsViewModel] ok", with no dedicated
        // assertion of its own. The line below is the second half — a path that only resolves against
        // PersonaSettingsViewModel if that re-root was understood — and it is what turns PersonasView's
        // coverage from incidental into asserted.
        Assert.Contains(bindings, b => b.Contains("=AddPersonaCommand [PersonaSettingsViewModel]"));

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
            BindingPathWalker.FindLogical<TextBlock>(view!).Select(tb => tb.Text).Where(t => t is not null).ToArray());

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
}
