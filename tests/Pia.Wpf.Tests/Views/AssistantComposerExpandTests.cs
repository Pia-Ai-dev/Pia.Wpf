using System.Windows;
using System.Windows.Controls;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The composer collapses to a few lines and offers to grow. The offer is driven by the drafted text's
/// measured height, so only a laid-out view can say whether it appears when it should.
/// </summary>
[Collection("WpfApplicationStatic")]
public class AssistantComposerExpandTests
{
    private const string ShortDraft = "one line";

    private static readonly string LongDraft =
        string.Join(Environment.NewLine, Enumerable.Range(1, 40).Select(i => $"line {i}"));

    [Fact]
    public void TheExpandToggleAppearsOnlyOnceTheDraftOutgrowsTheComposer()
    {
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        TextBox? input = null;
        Visibility shortDraft, longDraft, afterClearing;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                view = new AssistantView { DataContext = vm };
                Lay(view);
                input = (TextBox)view.FindName("InputTextBox");
                return 0;
            });
            WpfStaHost.Pump();

            shortDraft = Type(view!, input!, ShortDraft);
            longDraft = Type(view!, input!, LongDraft);
            // A send clears the draft, and the toggle must go with it.
            afterClearing = Type(view!, input!, string.Empty);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.Equal(Visibility.Collapsed, shortDraft);
        Assert.Equal(Visibility.Visible, longDraft);
        Assert.Equal(Visibility.Collapsed, afterClearing);
    }

    [Fact]
    public void ExpandingGrowsTheComposerAndCollapsingPutsItBack()
    {
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        TextBox? input = null;
        double collapsed, expanded, recollapsed;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                view = new AssistantView { DataContext = vm };
                Lay(view);
                input = (TextBox)view.FindName("InputTextBox");
                input.Text = LongDraft;
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();

            collapsed = WpfStaHost.Run(() => input!.MaxHeight);
            expanded = WpfStaHost.Run(() => { Click(view!); return input!.MaxHeight; });
            recollapsed = WpfStaHost.Run(() => { Click(view!); return input!.MaxHeight; });
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.True(expanded > collapsed, $"expanding left MaxHeight at {expanded}, was {collapsed}");
        Assert.Equal(collapsed, recollapsed);
    }

    /// <summary>Types <paramref name="text"/> and returns the expand toggle's visibility once it settles.</summary>
    private static Visibility Type(AssistantView view, TextBox input, string text)
    {
        WpfStaHost.Run(() =>
        {
            input.Text = text;
            Lay(view);
            return 0;
        });
        WpfStaHost.Pump();

        return WpfStaHost.Run(() => Button(view).Visibility);
    }

    private static void Click(AssistantView view) =>
        Button(view).RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static Wpf.Ui.Controls.Button Button(AssistantView view) =>
        (Wpf.Ui.Controls.Button)view.FindName("ComposerExpandButton");

    /// <summary>The view is never in a window, so nothing measures it unless the test does.</summary>
    private static void Lay(FrameworkElement view)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();
    }
}
