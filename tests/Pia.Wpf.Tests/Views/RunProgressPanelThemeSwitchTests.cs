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

/// <summary>A converter-resolved theme brush is a snapshot WPF freezes once its dictionary owns it, so a dictionary swap alone
/// cannot repaint the card. The light theme is restored in a <c>finally</c> because the Application is process-wide.</summary>
[Collection("WpfApplicationStatic")]
public class RunProgressPanelThemeSwitchTests
{
    /// <summary>The run is left settled on purpose: its bindings never move on their own, so only the theme notification
    /// can have caused a repaint.</summary>
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

        Assert.NotEqual(default, light.Band);
        Assert.NotEqual(default, light.Card);

        Assert.NotEqual(light.Band, dark.Band);
        Assert.NotEqual(light.BandBorder, dark.BandBorder);
        Assert.NotEqual(light.Card, dark.Card);
        Assert.NotEqual(light.StepGlyph, dark.StepGlyph);
    }

    /// <summary>The theme service is a singleton, so a leaked handler keeps a whole projected run alive for the process's life.</summary>
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
        // Located by declared binding, not by index, so a reordered template cannot silently pick another Border.
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
