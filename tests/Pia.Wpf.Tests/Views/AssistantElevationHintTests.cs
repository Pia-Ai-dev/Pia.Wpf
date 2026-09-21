using System.Windows;
using System.Windows.Controls;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Drives the laid-out view, not the view model: a typo in the strip's visibility binding leaves the
/// property under test correct and the strip permanently collapsed.
/// </summary>
[Collection("WpfApplicationStatic")]
public class AssistantElevationHintTests
{
    [Fact]
    public void TheStripFollowsTheElevationProbeAndTheDismissButton()
    {
        AssistantViewModel? elevatedVm = null, ordinaryVm = null;
        AssistantView? elevatedView = null, ordinaryView = null;
        Visibility elevated, ordinary, afterDismiss;

        try
        {
            WpfStaHost.Run(() =>
            {
                elevatedVm = AssistantViewModelBuilder.Create(elevation: Probe(true));
                elevatedView = new AssistantView { DataContext = elevatedVm };
                Lay(elevatedView);

                ordinaryVm = AssistantViewModelBuilder.Create(elevation: Probe(false));
                ordinaryView = new AssistantView { DataContext = ordinaryVm };
                Lay(ordinaryView);
                return 0;
            });
            WpfStaHost.Pump();

            elevated = VisibilityOf(elevatedView!);
            ordinary = VisibilityOf(ordinaryView!);

            WpfStaHost.Run(() =>
            {
                elevatedVm!.DismissElevatedSessionHintCommand.Execute(null);
                Lay(elevatedView!);
                return 0;
            });
            WpfStaHost.Pump();

            afterDismiss = VisibilityOf(elevatedView!);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                elevatedVm?.Dispose();
                ordinaryVm?.Dispose();
                return 0;
            });
        }

        Assert.Equal(Visibility.Visible, elevated);
        Assert.Equal(Visibility.Collapsed, ordinary);
        Assert.Equal(Visibility.Collapsed, afterDismiss);
    }

    private static IElevationService Probe(bool isElevated)
    {
        var probe = Substitute.For<IElevationService>();
        probe.IsElevated.Returns(isElevated);
        return probe;
    }

    private static Visibility VisibilityOf(AssistantView view) =>
        WpfStaHost.Run(() => ((Border)view.FindName("ElevatedSessionHint")).Visibility);

    private static void Lay(FrameworkElement view)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();
    }
}
