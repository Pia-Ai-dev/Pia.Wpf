using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>One navigation over the archive's heaviest chat cost 43–50 s and 3.2 GB, so every assertion
/// here counts elements — a time budget is flaky on CI and a count is not.</summary>
[Collection("WpfApplicationStatic")]
public class TranscriptRenderBudgetTests : IDisposable
{
    private const int WindowSize = 50;

    /// <summary>The heaviest chat in the imported archive, which is what the report was about.</summary>
    private const int Heaviest = 1573;

    private readonly List<AssistantViewModel> _viewModels = [];

    private AssistantViewModel _vm = null!;
    private AssistantView _view = null!;
    private ItemsControl _items = null!;
    private ScrollViewer _scroller = null!;
    private FrameworkElement _loadOlder = null!;

    [Fact]
    public void OpeningTheHeaviestChat_RealizesContainersForTheWindowOnly()
    {
        WpfStaHost.Run(() => OpenChatInView(Heaviest));
        WpfStaHost.Pump();

        var (whole, bound, realized, oldestBuilt, newestBuilt) = WpfStaHost.Run(() =>
            (_vm.Messages.Count, _items.Items.Count, RealizedContainers(),
             _items.ItemContainerGenerator.ContainerFromItem(_vm.Messages[0]) is not null,
             _items.ItemContainerGenerator.ContainerFromItem(_vm.Messages[^1]) is not null));

        Assert.Equal(Heaviest, whole);
        Assert.Equal(WindowSize, bound);
        Assert.Equal(WindowSize, realized);
        Assert.False(oldestBuilt, "the oldest message built a container, so the list still renders the whole chat");
        Assert.True(newestBuilt, "the newest message built no container, so the window is not the transcript's tail");
    }

    [Fact]
    public void TheRenderedTreeIsTheSameSizeForAShortChatAndTheHeaviestOne()
    {
        var (shortContainers, shortVisuals) = WpfStaHost.Run(() => RenderAndMeasure(WindowSize));
        WpfStaHost.Pump();
        var (heavyContainers, heavyVisuals) = WpfStaHost.Run(() => RenderAndMeasure(Heaviest));

        Assert.Equal(WindowSize, shortContainers);
        Assert.Equal(shortContainers, heavyContainers);
        // Without this the assertion below would hold on a list that rendered nothing at all.
        Assert.True(shortVisuals > WindowSize, $"{shortVisuals} visual elements for {WindowSize} messages — the item templates never built");
        Assert.Equal(shortVisuals, heavyVisuals);
    }

    /// <summary>The window is a view-side projection: <c>Messages</c> is also the export and the model's
    /// context, so it stays whole behind it.</summary>
    [Fact]
    public void AWindowedViewStillExportsAndStillSendsEveryMessage()
    {
        WpfStaHost.Run(() => OpenChatInView(Heaviest));
        WpfStaHost.Pump();

        var (bound, exported, sent) = WpfStaHost.Run(() =>
        {
            var chat = new SyncAssistantChat
            {
                Id = Guid.NewGuid(),
                Title = "heaviest",
                UpdatedAt = DateTime.UtcNow,
                Messages = [.. _vm.Messages.Select(AssistantMessageMapper.ToDto)],
            };

            return (_items.Items.Count,
                CountTurns(AssistantHistoryViewModel.BuildMarkdown(chat)),
                _vm.Messages.Select(m => m.ToChatMessage()).Count());
        });

        Assert.Equal(WindowSize, bound);
        Assert.Equal(Heaviest, exported);
        Assert.Equal(Heaviest, sent);
    }

    [Fact]
    public void AChatUnderTheWindow_RendersWholeAndStillOpensAtTheLatestTurn()
    {
        const int shortChat = 40;

        WpfStaHost.Run(() => OpenChatInView(shortChat));
        WpfStaHost.Pump();

        var (bound, realized, affordance, affordanceHeight, scrollable, offset) = WpfStaHost.Run(() =>
        {
            Lay();
            return (_items.Items.Count, RealizedContainers(), _loadOlder.Visibility, _loadOlder.ActualHeight,
                _scroller.ScrollableHeight, _scroller.VerticalOffset);
        });

        Assert.Equal(shortChat, bound);
        Assert.Equal(shortChat, realized);
        Assert.Equal(Visibility.Collapsed, affordance);
        Assert.Equal(0, affordanceHeight);
        Assert.True(scrollable > 0, "the chat did not overflow the viewport, so its scroll behaviour proves nothing");
        Assert.True(offset >= scrollable - 1, $"landed at {offset} of {scrollable}");
    }

    /// <summary>A view model outlives every view built over it, so a subscription left behind on discard
    /// roots the whole transcript tree — a production dump held 18 of them.</summary>
    [Fact]
    public void ADiscardedViewIsCollectedWhileItsViewModelLives()
    {
        System.Windows.Window? window = null;
        ContentPresenter? presenter = null;
        Button? parking = null;
        WeakReference? weak = null;
        bool collected;

        try
        {
            WpfStaHost.Run(() =>
            {
                _vm = NewViewModel();
                _vm.Messages = Transcript(120);
                _vm.HasMessages = true;
                (window, presenter, parking) = Host();

                // Held only as a weak reference: a local that survives the lambda is itself a root.
                var view = new AssistantView { DataContext = _vm };
                presenter.Content = view;
                window.UpdateLayout();
                weak = new WeakReference(view);
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                // The view focuses its composer on load, and the focused element is rooted by its window.
                Keyboard.Focus(parking);
                presenter!.Content = null;
                window!.UpdateLayout();
                return 0;
            });

            collected = Collect(weak!);
        }
        finally
        {
            WpfStaHost.Run(() => { window?.Close(); return 0; });
        }

        Assert.True(collected, "the discarded view is still rooted by the view model it was showing");
        Assert.Equal(120, _vm.Messages.Count);
        GC.KeepAlive(_vm);
    }

    private static bool Collect(WeakReference weak)
    {
        // Loaded and Unloaded both post work that captures the view, so it is only unrooted once the
        // queue has drained — which can take more than one pass.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            WpfStaHost.Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.IsAlive) return true;
        }

        return false;
    }

    private (int Containers, int Visuals) RenderAndMeasure(int messages)
    {
        OpenChatInView(messages);
        Lay();
        return (RealizedContainers(), CountVisuals(_items));
    }

    private int OpenChatInView(int messages)
    {
        _vm = NewViewModel();
        _view = new AssistantView { DataContext = _vm };
        Lay();
        // Nothing parents this view, so the hook that watches the view model for a chat swap is only taken
        // if the test raises Loaded itself.
        _view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, _view));
        _scroller = (ScrollViewer)_view.FindName("MessageScrollViewer");
        _items = (ItemsControl)_view.FindName("MessageItemsControl");
        _loadOlder = (FrameworkElement)_view.FindName("LoadOlderMessagesButton");

        _vm.Messages = Transcript(messages);
        _vm.HasMessages = messages > 0;
        Lay();
        return 0;
    }

    private AssistantViewModel NewViewModel()
    {
        var vm = AssistantViewModelBuilder.Create();
        _viewModels.Add(vm);
        return vm;
    }

    private int RealizedContainers()
    {
        var generator = _items.ItemContainerGenerator;
        return Enumerable.Range(0, _items.Items.Count).Count(i => generator.ContainerFromIndex(i) is not null);
    }

    private void Lay()
    {
        _view.Measure(new Size(900, 700));
        _view.Arrange(new Rect(0, 0, 900, 700));
        _view.UpdateLayout();
    }

    private static int CountVisuals(DependencyObject root)
    {
        var count = 1;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            count += CountVisuals(VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static int CountTurns(string markdown) =>
        markdown.Split('\n').Count(line => line.StartsWith("## ", StringComparison.Ordinal));

    // Roles run back from the newest turn, so two chats of different lengths give windows that differ only
    // in their text and a size comparison cannot be measuring the fixture.
    private static ObservableCollection<AssistantMessage> Transcript(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new AssistantMessage(
            (count - 1 - i) % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            $"turn {i:D4} — long enough to take a line of its own in the transcript"))];

    /// <summary>A real window, so WPF broadcasts Loaded and Unloaded the way a navigation does — raising
    /// them by hand reaches only the element they are raised on.</summary>
    private static (System.Windows.Window Window, ContentPresenter Presenter, Button Parking) Host()
    {
        var presenter = new ContentPresenter();
        var parking = new Button { Content = "park" };
        var grid = new Grid();
        grid.Children.Add(presenter);
        grid.Children.Add(parking);

        var window = new System.Windows.Window
        {
            Content = grid,
            Width = 600,
            Height = 400,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        return (window, presenter, parking);
    }

    public void Dispose()
    {
        WpfStaHost.Run(() =>
        {
            foreach (var vm in _viewModels)
                vm.Dispose();
            return 0;
        });
        GC.SuppressFinalize(this);
    }
}
