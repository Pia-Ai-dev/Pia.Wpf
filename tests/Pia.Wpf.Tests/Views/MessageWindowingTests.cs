using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>The list is bound to a bounded window over <c>Messages</c>, which itself stays whole because it
/// is the model's context, the export and in-chat search.</summary>
[Collection("WpfApplicationStatic")]
public class MessageWindowingTests : IDisposable
{
    private const int Window = 50;

    private AssistantViewModel _vm = null!;
    private AssistantView _view = null!;
    private ScrollViewer _scroller = null!;
    private ItemsControl _items = null!;
    private FrameworkElement _loadOlder = null!;
    private AssistantMessage _anchor = null!;

    [Fact]
    public void OpeningALongChat_BindsOnlyTheNewestWindow()
    {
        WpfStaHost.Run(() => OpenChatInView(120));
        WpfStaHost.Pump();

        var (visible, realized, whole, older, hasOlder, startsAt) = WpfStaHost.Run(() =>
            (_vm.VisibleMessages.Count, _items.Items.Count, _vm.Messages.Count, _vm.OlderMessageCount,
             _vm.HasOlderMessages, _vm.Messages.IndexOf(_vm.VisibleMessages[0])));

        Assert.Equal(Window, visible);
        Assert.Equal(Window, realized);
        Assert.Equal(120, whole);
        Assert.Equal(70, older);
        Assert.True(hasOlder);
        Assert.Equal(70, startsAt);
    }

    [Fact]
    public void AChatShorterThanTheWindow_ShowsNoAffordanceAtAll()
    {
        WpfStaHost.Run(() => OpenChatInView(12));
        WpfStaHost.Pump();

        var (visible, older, hasOlder, collapsed, height) = WpfStaHost.Run(() =>
            (_vm.VisibleMessages.Count, _vm.OlderMessageCount, _vm.HasOlderMessages,
             _loadOlder.Visibility == Visibility.Collapsed, _loadOlder.ActualHeight));

        Assert.Equal(12, visible);
        Assert.Equal(0, older);
        Assert.False(hasOlder);
        Assert.True(collapsed);
        Assert.Equal(0, height);
    }

    [Fact]
    public void LoadingOlder_PrependsOneWindowAtATime()
    {
        WpfStaHost.Run(() => OpenChatInView(120));
        WpfStaHost.Pump();

        var (afterOne, olderAfterOne, hasOlderAfterOne, startsAt) = WpfStaHost.Run(() =>
        {
            _vm.LoadOlderMessagesCommand.Execute(null);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount, _vm.HasOlderMessages,
                _vm.Messages.IndexOf(_vm.VisibleMessages[0]));
        });
        WpfStaHost.Pump();

        var (afterTwo, olderAfterTwo, hasOlderAfterTwo, whole) = WpfStaHost.Run(() =>
        {
            _vm.LoadOlderMessagesCommand.Execute(null);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount, _vm.HasOlderMessages, _vm.Messages.Count);
        });

        Assert.Equal(100, afterOne);
        Assert.Equal(20, olderAfterOne);
        Assert.True(hasOlderAfterOne);
        Assert.Equal(20, startsAt);

        Assert.Equal(120, afterTwo);
        Assert.Equal(0, olderAfterTwo);
        Assert.False(hasOlderAfterTwo);
        Assert.Equal(120, whole);
    }

    /// <summary>The anchor is a real measured container position, not the offset the code just wrote, so a
    /// correction that only looks right cannot pass this.</summary>
    [Fact]
    public void PrependingOlderMessages_LeavesTheReaderWhereTheyWere()
    {
        WpfStaHost.Run(() => OpenChatInView(120));
        WpfStaHost.Pump();

        var (scrollable, extentBefore, before) = WpfStaHost.Run(() =>
        {
            _scroller.ScrollToVerticalOffset(_scroller.ScrollableHeight / 2);
            Lay();
            _anchor = _vm.VisibleMessages[0];
            return (_scroller.ScrollableHeight, _scroller.ExtentHeight, AnchorY());
        });
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            _vm.LoadOlderMessagesCommand.Execute(null);
            return 0;
        });
        WpfStaHost.Pump();

        var (visible, extentAfter, settled) = WpfStaHost.Run(() =>
        {
            Lay();
            return (_vm.VisibleMessages.Count, _scroller.ExtentHeight, AnchorY());
        });
        WpfStaHost.Pump();

        // Markdown bubbles keep growing the extent for several passes, so a correction that only held for
        // the first one would still leave the reader adrift.
        var later = WpfStaHost.Run(() =>
        {
            Lay();
            return AnchorY();
        });

        Assert.True(scrollable > 0, "the window did not overflow the viewport, so there was nothing to anchor");
        Assert.Equal(100, visible);
        Assert.True(extentAfter > extentBefore,
            $"the prepended messages measured to no height ({extentBefore} -> {extentAfter}), so the offset " +
            "correction had nothing to correct and this test would pass on a no-op");
        Assert.True(Math.Abs(settled - before) < 2, $"the anchor moved from {before} to {settled}");
        Assert.True(Math.Abs(later - before) < 2, $"the anchor drifted to {later} on a later layout pass");
    }

    [Fact]
    public void RemovingTheTail_RefillsTheWindowFromOlderMessages()
    {
        WpfStaHost.Run(() => OpenChatInViewModel(120));

        var (visible, older, lastIndex, firstIndex) = WpfStaHost.Run(() =>
        {
            for (var i = _vm.Messages.Count - 1; i >= 118; i--)
                _vm.Messages.RemoveAt(i);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount,
                _vm.Messages.IndexOf(_vm.VisibleMessages[^1]),
                _vm.Messages.IndexOf(_vm.VisibleMessages[0]));
        });

        Assert.Equal(Window, visible);
        Assert.Equal(68, older);
        Assert.Equal(117, lastIndex);
        Assert.Equal(68, firstIndex);
    }

    /// <summary>Regenerating truncates the transcript back to the prompt, which can take the whole window
    /// with it.</summary>
    [Fact]
    public void TruncatingIntoTheWindow_RefillsItRatherThanEmptyingThePane()
    {
        WpfStaHost.Run(() => OpenChatInViewModel(120));

        var (visible, older, firstIndex, whole) = WpfStaHost.Run(() =>
        {
            for (var i = _vm.Messages.Count - 1; i >= 69; i--)
                _vm.Messages.RemoveAt(i);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount,
                _vm.Messages.IndexOf(_vm.VisibleMessages[0]), _vm.Messages.Count);
        });

        Assert.Equal(69, whole);
        Assert.Equal(Window, visible);
        Assert.Equal(19, older);
        Assert.Equal(19, firstIndex);
    }

    [Fact]
    public void AWindowTheReaderExpanded_IsNotShrunkBackByARemoval()
    {
        WpfStaHost.Run(() => OpenChatInViewModel(120));

        var (visible, older) = WpfStaHost.Run(() =>
        {
            _vm.LoadOlderMessagesCommand.Execute(null);
            for (var i = _vm.Messages.Count - 1; i >= 118; i--)
                _vm.Messages.RemoveAt(i);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount);
        });

        Assert.Equal(98, visible);
        Assert.Equal(20, older);
    }

    [Fact]
    public void RemovingBelowTheWindow_ShrinksOnlyTheOlderCount()
    {
        WpfStaHost.Run(() => OpenChatInViewModel(120));

        var (visible, older, hasOlder) = WpfStaHost.Run(() =>
        {
            _vm.Messages.RemoveAt(0);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount, _vm.HasOlderMessages);
        });

        Assert.Equal(Window, visible);
        Assert.Equal(69, older);
        Assert.True(hasOlder);
    }

    /// <summary>The refill holds the scroll where the reader was, which the turn that follows a regenerate
    /// has to override. The tail is the long half, so the refilled window is SHORTER than what it replaced
    /// and the correction it anchors on runs backwards.</summary>
    [Fact]
    public void TheTurnThatFollowsATruncation_StillLandsAtTheBottom()
    {
        WpfStaHost.Run(() => OpenChatInView(120, longFrom: 70));
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            for (var i = _vm.Messages.Count - 1; i >= 69; i--)
                _vm.Messages.RemoveAt(i);
            _vm.Messages.Add(new AssistantMessage(ChatRole.User, "ask it again"));
            _vm.Messages.Add(new AssistantMessage(ChatRole.Assistant, "the regenerated answer"));
            return 0;
        });
        WpfStaHost.Pump();

        var (offset, scrollable, following) = WpfStaHost.Run(() =>
        {
            Lay();
            return (_scroller.VerticalOffset, _scroller.ScrollableHeight, _view.IsAutoScrollEnabled);
        });

        Assert.True(scrollable > 0, "the transcript did not overflow the viewport, so nothing was scrolled");
        Assert.True(offset >= scrollable - 1, $"the new turn is off screen: {offset} of {scrollable}");
        Assert.True(following, "the answer would stream below a viewport that stopped following it");
    }

    [Fact]
    public void AnAppendAlwaysEntersTheWindow()
    {
        WpfStaHost.Run(() => OpenChatInViewModel(120));

        var (visible, older, isLast) = WpfStaHost.Run(() =>
        {
            var answer = new AssistantMessage(ChatRole.Assistant, "the newest turn");
            _vm.Messages.Add(answer);
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount,
                ReferenceEquals(_vm.VisibleMessages[^1], answer));
        });

        Assert.Equal(Window + 1, visible);
        Assert.Equal(70, older);
        Assert.True(isLast);
    }

    [Fact]
    public void ClearingTheTranscript_ClearsTheWindow()
    {
        WpfStaHost.Run(() => OpenChatInViewModel(120));

        var (visible, older, hasOlder) = WpfStaHost.Run(() =>
        {
            _vm.Messages.Clear();
            return (_vm.VisibleMessages.Count, _vm.OlderMessageCount, _vm.HasOlderMessages);
        });

        Assert.Equal(0, visible);
        Assert.Equal(0, older);
        Assert.False(hasOlder);
    }

    private int OpenChatInViewModel(int messages)
    {
        _vm = AssistantViewModelBuilder.Create();
        _vm.Messages = Transcript(messages);
        _vm.HasMessages = messages > 0;
        return 0;
    }

    private int OpenChatInView(int messages, int longFrom = int.MaxValue)
    {
        _vm = AssistantViewModelBuilder.Create();
        _view = new AssistantView { DataContext = _vm };
        Lay();
        // Nothing parents this view, so the hook that watches the view model for a chat swap is only taken
        // if the test raises Loaded itself.
        _view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, _view));
        _scroller = (ScrollViewer)_view.FindName("MessageScrollViewer");
        _items = (ItemsControl)_view.FindName("MessageItemsControl");
        _loadOlder = (FrameworkElement)_view.FindName("LoadOlderMessagesButton");

        _vm.Messages = Transcript(messages, longFrom);
        _vm.HasMessages = messages > 0;
        Lay();
        return 0;
    }

    private double AnchorY()
    {
        var container = (FrameworkElement?)_items.ItemContainerGenerator.ContainerFromItem(_anchor);
        return container is null
            ? double.NaN
            : container.TransformToAncestor(_scroller).Transform(default).Y;
    }

    private void Lay()
    {
        _view.Measure(new Size(900, 700));
        _view.Arrange(new Rect(0, 0, 900, 700));
        _view.UpdateLayout();
    }

    private static ObservableCollection<AssistantMessage> Transcript(int count, int longFrom = int.MaxValue) =>
        [.. Enumerable.Range(1, count).Select(i => new AssistantMessage(
            i % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            i > longFrom
                ? string.Join(" ", Enumerable.Repeat($"turn {i} ran to several lines of its own.", 30))
                : $"turn {i} — long enough to take a line of its own in the transcript"))];

    public void Dispose()
    {
        WpfStaHost.Run(() =>
        {
            _vm?.Dispose();
            return 0;
        });
        GC.SuppressFinalize(this);
    }
}
