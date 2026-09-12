using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.AssistantHistory;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The inspector's "load older" affordance is only reachable from the top of the pane, which is exactly
/// where an insert above the viewport displaces everything the reader is looking at.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ChatInspectorPrependAnchorTests : IDisposable
{
    private const int Window = 50;

    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly INavigationService _nav = Substitute.For<INavigationService>();
    private readonly global::Wpf.Ui.ISnackbarService _snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();
    private readonly IMarkdownExportService _markdownExport = Substitute.For<IMarkdownExportService>();

    private AssistantHistoryViewModel _vm = null!;
    private AssistantHistoryView _view = null!;
    private ScrollViewer _scroller = null!;
    private ItemsControl _items = null!;
    private Button _loadOlder = null!;
    private AssistantMessage _anchor = null!;

    [Fact]
    public void PrependingOlderMessages_LeavesTheReaderWhereTheyWere()
    {
        WpfStaHost.Run(() => OpenChat(120));
        WpfStaHost.Pump();

        var (visibleBefore, scrollable, extentBefore, before) = WpfStaHost.Run(() =>
        {
            _scroller.ScrollToVerticalOffset(_scroller.ScrollableHeight / 2);
            Lay();
            _anchor = _vm.VisibleChatMessages[0];
            return (_vm.VisibleChatMessages.Count, _scroller.ScrollableHeight, _scroller.ExtentHeight, AnchorY());
        });
        WpfStaHost.Pump();

        // The real button, not the command behind it: the correction hangs off Click, so executing the
        // command directly would leave this passing on a no-op.
        WpfStaHost.Run(() =>
        {
            ((IInvokeProvider)new ButtonAutomationPeer(_loadOlder).GetPattern(PatternInterface.Invoke)).Invoke();
            return 0;
        });
        WpfStaHost.Pump();

        var (visible, extentAfter, settled) = WpfStaHost.Run(() =>
        {
            Lay();
            return (_vm.VisibleChatMessages.Count, _scroller.ExtentHeight, AnchorY());
        });
        WpfStaHost.Pump();

        var later = WpfStaHost.Run(() =>
        {
            Lay();
            return AnchorY();
        });

        Assert.Equal(Window, visibleBefore);
        Assert.True(scrollable > 0, "the window did not overflow the viewport, so there was nothing to anchor");
        Assert.Equal(100, visible);
        Assert.True(extentAfter > extentBefore,
            $"the prepended messages measured to no height ({extentBefore} -> {extentAfter}), so the offset " +
            "correction had nothing to correct and this test would pass on a no-op");
        Assert.True(Math.Abs(settled - before) < 2, $"the anchor moved from {before} to {settled}");
        Assert.True(Math.Abs(later - before) < 2, $"the anchor drifted to {later} on a later layout pass");
    }

    private int OpenChat(int messages)
    {
        _vm = CreateSut(Chat(messages));
        _view = new AssistantHistoryView { DataContext = _vm };
        Lay();

        _vm.RefreshCommand.Execute(null);
        _vm.SelectedChat = _vm.Chats[0];
        Lay();

        var inspector = Descendants<PiaAssistantChatInspector>(_view).First();
        _scroller = (ScrollViewer)inspector.FindName("TranscriptScrollViewer");
        _items = (ItemsControl)inspector.FindName("TranscriptItemsControl");
        _loadOlder = (Button)inspector.FindName("LoadOlderMessagesButton");
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
        _view.Measure(new Size(1400, 700));
        _view.Arrange(new Rect(0, 0, 1400, 700));
        _view.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private AssistantHistoryViewModel CreateSut(params SyncAssistantChat[] chats)
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _sessions.GetState(Arg.Any<Guid>()).Returns(ChatState.Idle);
        _chatService.SearchAsync().ReturnsForAnyArgs(
            Task.FromResult<IReadOnlyList<SyncAssistantChat>>(chats));
        _chatService.CountAsync().ReturnsForAnyArgs(Task.FromResult(chats.Length));
        _chatService.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            ci => Task.FromResult(chats.FirstOrDefault(c => c.Id == (Guid)ci[0])));
        _providers.GetProvidersAsync().Returns(
            Task.FromResult<IReadOnlyList<AiProvider>>([]));

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService, _providers, _dialog, _loc, _nav, _snackbar, _sessions, _markdownExport,
            Substitute.For<IChatArchiveService>());
    }

    private static SyncAssistantChat Chat(int messages) => new()
    {
        Id = Guid.NewGuid(),
        Title = "imported",
        UpdatedAt = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc),
        Messages = [.. Enumerable.Range(1, messages).Select(Message)],
    };

    private static SyncAssistantChatMessage Message(int index) => new()
    {
        Id = Guid.NewGuid(),
        Role = index % 2 == 0 ? "assistant" : "user",
        Content = $"turn {index} — long enough to take a line of its own in the transcript",
        Timestamp = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc).AddSeconds(index),
    };

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
