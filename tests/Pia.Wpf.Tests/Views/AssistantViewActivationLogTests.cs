using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>A "switching is slow" report has to arrive with its own cause: the build time and how many
/// messages were rendered for it.</summary>
[Collection("WpfApplicationStatic")]
public class AssistantViewActivationLogTests
{
    // Seven cannot be mistaken for an elapsed-millisecond figure in the same line.
    private const int MessageCount = 7;

    [Fact]
    public void ActivatingTheViewLogsTheMessageCount()
    {
        var logger = new CapturingLogger<AssistantViewModel>();
        AssistantViewModel? vm = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create(logger);
                vm.Messages = Transcript(MessageCount);
                vm.HasMessages = true;

                var view = new AssistantView { DataContext = vm };
                Lay(view);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        var line = Assert.Single(ActivationLines(logger));
        Assert.Contains($"{MessageCount} messages", line);
    }

    [Fact]
    public void ReParentingTheViewDoesNotLogASecondActivation()
    {
        var logger = new CapturingLogger<AssistantViewModel>();
        AssistantViewModel? vm = null;
        AssistantView? view = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create(logger);
                vm.Messages = Transcript(MessageCount);
                vm.HasMessages = true;

                view = new AssistantView { DataContext = vm };
                Lay(view);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                // A re-parent raises Loaded again without an Unloaded, which is not a second navigation.
                view!.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.Single(ActivationLines(logger));
    }

    // The manager's activation completes after the view loaded, so the activation line above reports an
    // empty transcript. The render that costs the time is the one this logs.
    [Fact]
    public void RePointingMessagesAfterLoad_LogsTheTranscriptItActuallyBuilt()
    {
        var logger = new CapturingLogger<AssistantViewModel>();
        AssistantViewModel? vm = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create(logger);

                var view = new AssistantView { DataContext = vm };
                Lay(view);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                vm!.Messages = Transcript(MessageCount);
                vm.HasMessages = true;
                return 0;
            });
            WpfStaHost.Pump();
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.Contains("0 messages", Assert.Single(ActivationLines(logger)));
        Assert.Contains($"of {MessageCount} messages", Assert.Single(TranscriptLines(logger)));
    }

    [Fact]
    public void ASecondChatRePointsAndLogsAgain()
    {
        var logger = new CapturingLogger<AssistantViewModel>();
        AssistantViewModel? vm = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create(logger);
                vm.Messages = Transcript(MessageCount);
                vm.HasMessages = true;

                var view = new AssistantView { DataContext = vm };
                Lay(view);
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                Lay(view);
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                vm!.Messages = Transcript(MessageCount + 1);
                return 0;
            });
            WpfStaHost.Pump();
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        // Switching chats is the reported symptom, so each switch has to name its own transcript.
        Assert.Contains($"of {MessageCount + 1} messages", Assert.Single(TranscriptLines(logger)));
    }

    private static IReadOnlyList<string> TranscriptLines(CapturingLogger<AssistantViewModel> logger) =>
        [.. logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("Assistant transcript built"))
            .Select(e => e.Message)];

    private static IReadOnlyList<string> ActivationLines(CapturingLogger<AssistantViewModel> logger) =>
        [.. logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("Assistant view activated"))
            .Select(e => e.Message)];

    private static ObservableCollection<AssistantMessage> Transcript(int turns) =>
        [.. Enumerable.Range(1, turns).Select(i => new AssistantMessage(
            i % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            $"turn {i} — long enough to take a line of its own in the transcript"))];

    private static void Lay(FrameworkElement view)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();
    }
}
