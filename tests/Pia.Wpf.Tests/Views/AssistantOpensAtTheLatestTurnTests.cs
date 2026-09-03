using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A chat opened from history or the picker arrives as a re-pointed, already-populated collection, so no
/// Add is raised and the scroller has nothing to react to. It has to land at the newest turn anyway.
/// </summary>
[Collection("WpfApplicationStatic")]
public class AssistantOpensAtTheLatestTurnTests
{
    [Fact]
    public void ReOpeningAChatLandsAtTheBottom()
    {
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        ScrollViewer? scroller = null;
        double scrollable, offset;
        bool scrolledAway;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                view = new AssistantView { DataContext = vm };
                Lay(view);
                // Nothing parents this view, so the hook that watches the view model for a chat swap
                // is only taken if the test raises Loaded itself.
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                scroller = (ScrollViewer)view.FindName("MessageScrollViewer");

                // A chat is already open, and the reader has scrolled up in it. That is the state the
                // report is about: without it the view is already at the bottom and proves nothing.
                vm.Messages = Transcript(60, "first");
                vm.HasMessages = true;
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();

            scrolledAway = WpfStaHost.Run(() =>
            {
                scroller!.ScrollToVerticalOffset(0);
                Lay(view!);
                return !view!.IsAutoScrollEnabled;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                // Opening another chat: the collection is re-pointed, already populated, so nothing
                // is added and the scroller has no event of its own to react to.
                vm!.Messages = Transcript(60, "second");
                Lay(view!);
                return 0;
            });
            WpfStaHost.Pump();

            (scrollable, offset) = WpfStaHost.Run(() =>
            {
                Lay(view!);
                return (scroller!.ScrollableHeight, scroller.VerticalOffset);
            });
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.True(scrolledAway, "the reader's scroll to the top did not disarm auto-scroll, so the " +
            "assertion below would hold without the fix");
        // Without this the test would pass on an unscrollable viewport, proving nothing.
        Assert.True(scrollable > 0, "the transcript did not overflow the viewport, so there was nothing to scroll");
        Assert.True(offset >= scrollable - 1, $"landed at {offset} of {scrollable}");
    }

    private static ObservableCollection<AssistantMessage> Transcript(int turns, string chat) =>
        [.. Enumerable.Range(1, turns).Select(i => new AssistantMessage(
            i % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            $"{chat} chat, turn {i} — long enough to take a line of its own in the transcript"))];

    /// <summary>The view is never in a window, so nothing measures it unless the test does.</summary>
    private static void Lay(FrameworkElement view)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();
    }
}
