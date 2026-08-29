using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Extensions.AI;
using Pia.Behaviors;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A chat reopened from history rendered every message but reported none of them to UIA — a screen reader
/// and a UI script both saw an empty conversation. The messages arrive in one go while a client is attached,
/// their peers materialize before anything has measured the containers, and each one caches an empty subtree.
/// There is deliberately no "and without the behavior it stays empty" arm: WPF's own post-layout automation
/// update repairs it in some processes and not others (it is gated on clients listening), so that arm passes
/// or fails on the environment rather than on this code. The live proof is a walkthrough; these two hold the
/// wiring and the recovery.
/// </summary>
[Collection("WpfApplicationStatic")]
public class MessageListPeerRefreshTests
{
    private readonly AssistantMessage _first = new(ChatRole.User, "first");
    private readonly AssistantMessage _second = new(ChatRole.User, "second");
    private Pia.Views.AssistantView _view = null!;
    private AutomationPeer _root = null!;

    [Fact]
    public void MessagesLoadedIntoAnAlreadyWalkedView_AreStillReadable()
    {
        WpfStaHost.Run(LoadAChatAfterThePeersExist);
        WpfStaHost.Pump();

        Assert.Equal(
            [$"Assistant_CopyMessage_{_first.Id}", $"Assistant_CopyMessage_{_second.Id}"],
            WpfStaHost.Run(Observe));
    }

    /// <summary>The recovery above only ever runs if the markup still asks for it.</summary>
    [Fact]
    public void TheMessageList_AsksForThePeerRefresh()
    {
        Assert.True(WpfStaHost.Run(() =>
            AutomationPeerRefreshBehavior.GetRefreshOnContainersGenerated(
                new Pia.Views.AssistantView().MessageItemsControl)));
    }

    private bool LoadAChatAfterThePeersExist()
    {
        var chat = new MessagesStub();
        _view = new Pia.Views.AssistantView { DataContext = chat };

        // An empty chat, walked by the attached client: the state a "New chat" leaves behind.
        Layout();
        _root = UIElementAutomationPeer.CreatePeerForElement(_view)
            ?? throw new InvalidOperationException("AssistantView no longer creates an automation peer");
        Observe();

        chat.Messages.Add(_first);
        chat.Messages.Add(_second);

        // The premise, asserted rather than assumed: the collection change generates the containers
        // synchronously, but nothing has measured them, so their content is not in the tree yet and the walk
        // below is what caches an empty subtree per message.
        Assert.Equal(0, Measured());
        Assert.Empty(Observe());

        chat.Reveal();
        Layout();
        Assert.Equal(2, Measured());
        return true;
    }

    private void Layout()
    {
        _view.Measure(new Size(1000, 900));
        _view.Arrange(new Rect(0, 0, 1000, 900));
        _view.UpdateLayout();
    }

    /// <summary>How many message containers have been through a layout pass — an unmeasured one has no
    /// content in the tree for a peer to wrap.</summary>
    private int Measured()
    {
        var generator = _view.MessageItemsControl.ItemContainerGenerator;
        return Enumerable.Range(0, _view.MessageItemsControl.Items.Count)
            .Select(generator.ContainerFromIndex)
            .OfType<FrameworkElement>()
            .Count(c => c.ActualWidth > 0);
    }

    private string[] Observe()
    {
        var found = new List<string>();
        Collect(_root, found);
        return [.. found];
    }

    private static void Collect(AutomationPeer peer, List<string> found)
    {
        if (peer.GetAutomationId() is { } id && id.StartsWith("Assistant_CopyMessage_", StringComparison.Ordinal))
            found.Add(id);

        foreach (var child in peer.GetChildren() ?? [])
            Collect(child, found);
    }

    /// <summary>Only what the message list binds; the rest of the view's bindings resolve to nothing, which
    /// is a binding trace, not a failure. HasMessages is raised on demand rather than from the collection so
    /// the test owns the moment the list stops being collapsed.</summary>
    private sealed class MessagesStub : INotifyPropertyChanged
    {
        public ObservableCollection<AssistantMessage> Messages { get; } = [];

        public bool HasMessages { get; private set; }

        public void Reveal()
        {
            HasMessages = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMessages)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
