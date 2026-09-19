using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Extensions.AI;
using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>The dropdown closes when the chat scrolls out from under it — but a Popup routes its events
/// into the placement target's tree, so the list's own scroller reaches that same handler.</summary>
[Collection("WpfApplicationStatic")]
public class ChipOverflowPopupScrollTests
{
    [Fact]
    public void ScrollingTheDropdown_LeavesItOpen_ScrollingTheChatClosesIt()
    {
        var message = new AssistantMessage(ChatRole.Assistant, "answer");
        for (var i = 0; i < 30; i++)
            message.FileRefs.Add(new FileRef($@"C:\work\file{i:00}.md", FileRefKind.Read));

        HwndSource? source = null;
        ScrollViewer? host = null;
        Popup? popup = null;
        ScrollViewer? list = null;
        var reachedHost = new List<(object? Origin, double Vertical)>();

        try
        {
            WpfStaHost.Run(() =>
            {
                var view = new PiaAssistantMessage { DataContext = message };

                host = new ScrollViewer
                {
                    Width = 600,
                    Height = 300,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new StackPanel { Children = { view, new Border { Height = 2000 } } },
                };

                // Not shown (no WS_VISIBLE), but a real source: a Popup only realizes its child under one.
                source = new HwndSource(new HwndSourceParameters("PiaChipOverflowPopupScrollTests")
                {
                    Width = 800,
                    Height = 600,
                    WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                })
                {
                    RootVisual = host,
                };

                host.UpdateLayout();

                var panel = BindingPathWalker.FindLogical<PiaChipOverflowPanel>(view)
                    .Single(p => BindingPathWalker.PathOf(p, PiaChipOverflowPanel.ItemsSourceProperty)
                        == nameof(AssistantMessage.FileRefs));

                popup = (Popup)panel.FindName("MorePopup");
                var more = (Button)panel.FindName("MoreButton");
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, more));
                return 0;
            });
            WpfStaHost.Pump();

            var opened = WpfStaHost.Run(() =>
            {
                list = BindingPathWalker.FindLogical<ScrollViewer>(popup!.Child).SingleOrDefault();
                list?.UpdateLayout();

                host!.ScrollChanged += (_, e) => reachedHost.Add((e.OriginalSource, e.VerticalChange));
                return popup.IsOpen && list is not null;
            });

            Assert.True(opened, "the dropdown did not open over its list");

            WpfStaHost.Run(() => { list!.ScrollToVerticalOffset(120); return 0; });
            WpfStaHost.Pump();

            var openAfterListScroll = WpfStaHost.Run(() => popup!.IsOpen);
            var listReachedHost = WpfStaHost.Run(() =>
                reachedHost.Any(e => ReferenceEquals(e.Origin, list) && e.Vertical != 0));

            // The premise: without it the leg below passes on a list that never scrolled at all.
            Assert.True(listReachedHost,
                "the dropdown's scroll never reached the chat scroller, so this test cannot see the bug");
            Assert.True(openAfterListScroll, "scrolling the dropdown closed it");

            WpfStaHost.Run(() => { host!.ScrollToVerticalOffset(200); return 0; });
            WpfStaHost.Pump();

            Assert.False(WpfStaHost.Run(() => popup!.IsOpen),
                "scrolling the chat left the dropdown hanging over the messages it no longer belongs to");
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                if (popup is not null) popup.IsOpen = false;
                source?.Dispose();
                return 0;
            });
        }
    }
}
