using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Views.Dialogs.Overlay;
using Pia.Views.Overlays;
using Xunit;

namespace Pia.Tests.Views;

[Collection("WpfApplicationStatic")]
public class PolicyRestartOverlayPanelTests
{
    /// <summary>The user-visible guarantee: one keystroke must not dismiss a forcing overlay.</summary>
    [Fact]
    public void Escape_NeitherDismissesNorRestarts()
    {
        var observed = RealizeAndObserve(panel => panel.OnEscapePressed());

        Assert.Equal("0|0|True", observed);
    }

    /// <summary>The second guard on Escape: the base implementation raises Close, so relaxing this arm turns
    /// one lost override into a dismissable forcing overlay.</summary>
    [Fact]
    public void ANonPrimaryResult_NeitherDismissesNorRestarts()
    {
        var observed = RealizeAndObserve(panel =>
            Descendants(panel).OfType<Wpf.Ui.Controls.Button>()
                .First(b => b.Name == "PART_CloseButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));

        Assert.Equal("0|0|True", observed);
    }

    /// <summary>Pinned in source because the guard is currently inert: the result filter swallows Close either
    /// way, so deleting the override is invisible until someone lets a non-Primary result through.</summary>
    [Fact]
    public void TheEscapeOverrideIsStillDeclaredAsANoOp()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceRoot(), "Views", "Dialogs", "Overlay", "PolicyRestartOverlayPanel.xaml.cs"));

        var body = Regex.Match(
            source,
            @"public override void OnEscapePressed\(\)\s*\{(?<body>[^}]*)\}");

        Assert.True(body.Success, "PolicyRestartOverlayPanel no longer overrides OnEscapePressed");
        Assert.True(string.IsNullOrWhiteSpace(body.Groups["body"].Value),
            "the OnEscapePressed override is no longer a no-op");
    }

    /// <summary>Results, restart requests and the button's enabled state after one interaction with a
    /// realized panel — the three things that distinguish "inert" from "dismissed" or "restarting".</summary>
    private static string RealizeAndObserve(Action<PolicyRestartOverlayPanel> interact)
    {
        PolicyRestartOverlayPanel? panel = null;
        var results = 0;
        var restarts = 0;

        WpfStaHost.Run(() =>
        {
            panel = new PolicyRestartOverlayPanel();
            panel.ResultChosen += _ => results++;
            panel.RestartRequested += (_, _) => restarts++;
            panel.Measure(new Size(640, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
            panel.UpdateLayout();
            return 0;
        });
        WpfStaHost.Pump();

        return WpfStaHost.Run(() =>
        {
            interact(panel!);
            return $"{results}|{restarts}|{panel!.IsPrimaryButtonEnabled}";
        });
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pia.Wpf")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Pia.Wpf");
    }

    /// <summary>Null text is what collapses the template parts, so "one button, no escape" stays declarative.</summary>
    [Fact]
    public void OnlyThePrimaryButtonTextIsDeclared()
    {
        PolicyRestartOverlayPanel? panel = null;
        WpfStaHost.Run(() =>
        {
            panel = new PolicyRestartOverlayPanel();
            return 0;
        });
        WpfStaHost.Pump();

        var texts = WpfStaHost.Run(() => string.Join(
            "|",
            panel!.PrimaryButtonText ?? "<null>",
            panel.SecondaryButtonText ?? "<null>",
            panel.CloseButtonText ?? "<null>"));

        var parts = texts.Split('|');
        Assert.False(string.IsNullOrWhiteSpace(parts[0]));
        // LocalizationSource yields "[Key]" when the resx entry is missing.
        Assert.DoesNotContain("[", parts[0]);
        Assert.Equal("<null>", parts[1]);
        Assert.Equal("<null>", parts[2]);
    }

    /// <summary>A real layout pass over the inherited template: a bad loc key, a missing style or a lost
    /// default style key all fail here instead of at runtime.</summary>
    [Fact]
    public void ThePanelRealizesWithTheRestartButtonAsItsOnlyVisibleButton()
    {
        PolicyRestartOverlayPanel? panel = null;
        int buttonCount;
        string visibleButtons;
        bool labelMatchesPrimaryText;

        WpfStaHost.Run(() =>
        {
            panel = new PolicyRestartOverlayPanel();
            return 0;
        });
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            panel!.Measure(new Size(640, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
            panel.UpdateLayout();
            return 0;
        });
        WpfStaHost.Pump();

        (buttonCount, visibleButtons, labelMatchesPrimaryText) = WpfStaHost.Run(() =>
        {
            var buttons = Descendants(panel!).OfType<Wpf.Ui.Controls.Button>().ToArray();
            var visible = buttons.Where(b => b.Visibility == Visibility.Visible).ToArray();
            return (
                buttons.Length,
                string.Join(",", visible.Select(b => b.Name)),
                visible.Length == 1 && Equals(visible[0].Content, panel!.PrimaryButtonText));
        });

        // Non-vacuity: an untemplated panel realizes no buttons at all, which "one visible" would hide.
        Assert.Equal(3, buttonCount);
        Assert.Equal("PART_PrimaryButton", visibleButtons);
        Assert.True(labelMatchesPrimaryText,
            "the only visible button did not carry the panel's PrimaryButtonText");
    }

    /// <summary>The host collapses its content the instant a result is raised, so a Primary result would
    /// hand the user a live, unlocked app for the whole pre-exit sequence. The panel asks for the restart
    /// and stays up instead. A second press must not ask twice.</summary>
    [Fact]
    public void PressingRestart_RaisesNoResultAndAsksForTheRestartInstead()
    {
        PolicyRestartOverlayPanel? panel = null;
        var results = 0;
        var restarts = 0;

        WpfStaHost.Run(() =>
        {
            panel = new PolicyRestartOverlayPanel();
            panel.ResultChosen += _ => results++;
            panel.RestartRequested += (_, _) => restarts++;
            panel.Measure(new Size(640, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
            panel.UpdateLayout();
            return 0;
        });
        WpfStaHost.Pump();

        var observed = WpfStaHost.Run(() =>
        {
            var primary = Descendants(panel!).OfType<Wpf.Ui.Controls.Button>()
                .First(b => b.Name == "PART_PrimaryButton");

            primary.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            primary.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            return string.Join(
                "|",
                results.ToString(),
                restarts.ToString(),
                panel!.IsPrimaryButtonEnabled.ToString(),
                panel.PrimaryButtonText ?? "<null>");
        });

        var parts = observed.Split('|');
        Assert.Equal("0", parts[0]);
        Assert.Equal("1", parts[1]);
        Assert.Equal(false.ToString(), parts[2]);
        Assert.DoesNotContain("[", parts[3]);
    }

    /// <summary>The presenter's own panel build, driven end to end: the test double the presenter suite uses
    /// replaces this step, so without this the wiring from the button to the restart is unexercised.</summary>
    [Fact]
    public void ThePresentersPanel_AsksTheRestartServiceWhenTheButtonIsPressed()
    {
        var restart = Substitute.For<IAppRestartService>();
        using var presenter = new PolicyRestartOverlayPresenter(
            Substitute.For<IPolicyService>(),
            Substitute.For<IDialogOverlayService>(),
            Substitute.For<IDirectTranscriptionService>(),
            Substitute.For<IMeetingAttendeeService>(),
            Substitute.For<IExecutingRunStore>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IVolatileWorkStore>(),
            restart,
            new Pia.Tests.Services.InlineUiDispatcher(),
            NullLogger<PolicyRestartOverlayPresenter>.Instance);

        PolicyRestartOverlayPanel? panel = null;
        WpfStaHost.Run(() =>
        {
            panel = presenter.CreatePanel(restart.RestartAsync);
            panel.Measure(new Size(640, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
            panel.UpdateLayout();
            return 0;
        });
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            Descendants(panel!).OfType<Wpf.Ui.Controls.Button>()
                .First(b => b.Name == "PART_PrimaryButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return 0;
        });
        WpfStaHost.Pump();

        restart.Received(1).RestartAsync();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
