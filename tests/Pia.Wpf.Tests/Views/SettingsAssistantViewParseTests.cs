using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>A misspelled <c>Binding</c> path renders a control that silently never persists, so each declared
/// path is checked against the sub-ViewModel the markup re-roots it at, without constructing a ViewModel.</summary>
[Collection("WpfApplicationStatic")]
public class SettingsAssistantViewParseTests
{
    /// <summary>A floor, not a count: "no unresolved paths" is vacuously true over an empty walk, and a
    /// container swapped for a templated one stops a logical walk dead.</summary>
    private const int MinimumBoundPaths = 20;

    /// <summary>No test calls SetCulture, so the neutral resx is deterministically what the view renders.</summary>
    private const string RosterHeaderText = "Step specialists";

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        // Only the TYPE is checked; nothing here opens SettingsView.xaml, so a re-host would leave every path
        // below resolving against a type the markup no longer uses (ViewHostDataContextTests reads that site).
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

        Assert.Contains(bindings, b => b.Contains("=AgentPlanReasoningTurnEnabled "));
        Assert.Contains(bindings, b => b.Contains("=AgentRunAutoApproveBuiltInWrites "));
        Assert.Contains(bindings, b => b.Contains("=AgentRosterOptions "));

        // The nested PersonasView has no parse test of its own; this path only resolves against
        // PersonaSettingsViewModel if the re-root above it was understood.
        Assert.Contains(bindings, b => b.Contains("=AddPersonaCommand [PersonaSettingsViewModel]"));

        // The scheduled-jobs section moved out to the Routines view; the budget sliders above it did not, and
        // they are what the two asserts below still hold here.
        Assert.Contains(bindings, b => b.Contains("=ScheduledMaxSteps "));
        Assert.Contains(bindings, b => b.Contains("=MaxParallelBackgroundRuns "));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Views/SettingsViews/AssistantView.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void ParsedView_HasNoUnresolvedLocalizationKeys()
    {
        // LocalizationSource returns the literal "[Key]" for an unknown key. Only loc:Str bound to
        // TextBlock.Text is visible to a logical walk, so Content= labels are out of scope until templating.
        Pia.Views.SettingsViews.AssistantView? view = null;
        WpfStaHost.Run(() =>
        {
            view = new Pia.Views.SettingsViews.AssistantView();
            return 0;
        });
        WpfStaHost.Pump();

        var rendered = WpfStaHost.Run(() =>
            BindingPathWalker.FindLogical<TextBlock>(view!).Select(tb => tb.Text).Where(t => t is not null).ToArray());

        // An unbound TextBlock.Text is "" and not null, so a count-only floor would sweep a page of empty
        // strings clean forever; anchoring on a string the view really renders puts the drain under test.
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
