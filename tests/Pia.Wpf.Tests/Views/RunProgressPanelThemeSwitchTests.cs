using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A live theme switch has to repaint the run card, including the surfaces whose colour came from a
/// <see cref="System.Windows.Data.IValueConverter"/>.
/// <para>
/// <b>The defect this exists for.</b> A converter that resolves a theme brush by key returns a SNAPSHOT — it
/// re-runs only when its source value changes — and a dictionary swap cannot fix that snapshot in place, because
/// WPF freezes freezables once their dictionary is owned (so neither recolouring the brush nor giving it a
/// <c>DynamicResource</c> colour works; both were measured while writing this). On this card that reaches the
/// band's tint, its hairline, the card outline and every state and status foreground — so a light→dark switch left
/// a light band sitting on a dark card until the run's state next happened to move, which on a settled run is
/// never. <c>IThemeService.ThemeChanged</c> plus <c>RunProgressViewModel.RefreshThemeBrushes</c> is the fix, and
/// this fact is what proves it reaches the rendered visual rather than just the ViewModel.
/// </para>
/// <para>
/// Restores the light theme in a <c>finally</c>: the <see cref="Application"/> is process-wide and outlives this
/// class, and the collection is shared with every other view fact.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class RunProgressPanelThemeSwitchTests
{
    /// <summary>
    /// <b>REGRESSION.</b> The band's rendered Background and BorderBrush, and the card's outline, all change colour
    /// across a switch — read off the laid-out visual, never off the ViewModel. The run is left COMPLETED, i.e.
    /// settled, deliberately: that is the state whose bindings never move on their own, so nothing but the theme
    /// notification can have caused the repaint.
    /// <para>Neutralize: drop the <c>ThemeChanged</c> subscription in <c>RunProgressViewModel</c>'s constructor —
    /// every leg reds, because every one of these brushes is converter-resolved.</para>
    /// </summary>
    [Fact]
    public async Task ASettledCardRepaintsItsBandAndOutlineWhenTheThemeChanges()
    {
        var theme = new ThemeService(NullLogger<ThemeService>.Instance);
        var runId = Guid.NewGuid();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = runId,
            State = AgentRunState.Completed,
            Plan = [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "Read notes", Status = AgentStepStatus.Done }],
        });

        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        (Color Band, Color BandBorder, Color Card, Color StepGlyph) light, dark;

        try
        {
            WpfStaHost.Run(() => { theme.ApplyTheme(AppTheme.Light); return 0; });

            WpfStaHost.Run(() =>
            {
                var loc = Substitute.For<ILocalizationService>();
                loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
                loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
                vm = new RunProgressViewModel(runs, runId, loc, Substitute.For<IAgentRunResumeService>(),
                    NullLogger.Instance, themeService: theme);
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });

            await WpfStaHost.Run(() => vm!.RefreshAsync());
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                panel!.Measure(new Size(640, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
                panel.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            light = WpfStaHost.Run(() => Probe(panel!));

            WpfStaHost.Run(() => { theme.ApplyTheme(AppTheme.Dark); return 0; });
            WpfStaHost.Pump();
            WpfStaHost.Run(() => { panel!.UpdateLayout(); return 0; });

            dark = WpfStaHost.Run(() => Probe(panel!));
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                theme.ApplyTheme(AppTheme.Light);
                return 0;
            });
        }

        // Non-vacuity first: a fully transparent read would satisfy "they differ" for the wrong reason.
        Assert.NotEqual(default, light.Band);
        Assert.NotEqual(default, light.Card);

        Assert.NotEqual(light.Band, dark.Band);
        Assert.NotEqual(light.BandBorder, dark.BandBorder);
        Assert.NotEqual(light.Card, dark.Card);
        Assert.NotEqual(light.StepGlyph, dark.StepGlyph);
    }

    /// <summary>
    /// The other half: a VM that has been DISPOSED must stop listening. The theme service is a singleton, so a
    /// leaked handler keeps a whole projected run and every one of its rows alive for the process's life — and the
    /// run card is constructed anew for every chat the user opens.
    /// </summary>
    [Fact]
    public void ADisposedPanelViewModelStopsListeningForThemeChanges()
    {
        var theme = Substitute.For<IThemeService>();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentRun?)null);

        var vm = new RunProgressViewModel(runs, Guid.NewGuid(), Substitute.For<ILocalizationService>(),
            Substitute.For<IAgentRunResumeService>(), NullLogger.Instance, themeService: theme);

        theme.Received(1).ThemeChanged += Arg.Any<EventHandler>();

        vm.Dispose();

        theme.Received(1).ThemeChanged -= Arg.Any<EventHandler>();
    }

    private static (Color Band, Color BandBorder, Color Card, Color StepGlyph) Probe(RunProgressPanel panel)
    {
        // Located structurally, never by index: the card is the outermost Border, the band is the one Border that
        // binds BOTH Background and BorderBrush to State, and the step glyph is the icon inside the realized row.
        var borders = FindVisual<Border>(panel).ToList();
        var card = borders.First(b =>
            BindingPathWalker.PathOf(b, Border.BorderBrushProperty) == "State" &&
            BindingPathWalker.PathOf(b, Border.BackgroundProperty) is null);
        var band = borders.First(b =>
            BindingPathWalker.PathOf(b, Border.BackgroundProperty) == "State" &&
            BindingPathWalker.PathOf(b, Border.BorderBrushProperty) == "State");
        var glyph = FindVisual<global::Wpf.Ui.Controls.SymbolIcon>(panel)
            .First(i => BindingPathWalker.PathOf(i, Control.ForegroundProperty) == "Status");

        return (
            (band.Background as SolidColorBrush)?.Color ?? default,
            (band.BorderBrush as SolidColorBrush)?.Color ?? default,
            (card.BorderBrush as SolidColorBrush)?.Color ?? default,
            (glyph.Foreground as SolidColorBrush)?.Color ?? default);
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) yield return hit;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var found in FindVisual<T>(VisualTreeHelper.GetChild(root, i)))
                yield return found;
        }
    }
}
