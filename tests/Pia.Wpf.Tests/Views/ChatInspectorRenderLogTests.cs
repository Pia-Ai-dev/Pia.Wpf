using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using NSubstitute;
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
/// A round trip through history renders the transcript on both sides, so a "switching is slow" report
/// needs the inspector's render in the log too — not just the assistant view's.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ChatInspectorRenderLogTests : IDisposable
{
    private const int Window = 50;

    private readonly CapturingLogger<AssistantHistoryViewModel> _logger = new();
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

    [Fact]
    public void SelectingAChat_LogsWhatTheInspectorRendered()
    {
        WpfStaHost.Run(() => OpenChats(120));
        WpfStaHost.Pump();

        var line = Assert.Single(RenderLines());
        Assert.Contains($"showing {Window} of 120 messages", line);
    }

    [Fact]
    public void SelectingASecondChat_LogsItsOwnRender()
    {
        WpfStaHost.Run(() => OpenChats(120, 7));
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            _vm.SelectedChat = _vm.Chats[1];
            Lay();
            return 0;
        });
        WpfStaHost.Pump();

        Assert.Equal(2, RenderLines().Count);
        Assert.Contains("showing 7 of 7 messages", RenderLines()[1]);
    }

    private IReadOnlyList<string> RenderLines() =>
        [.. _logger.Entries
            .Where(e => e.Level == LogLevel.Information
                        && e.Message.Contains("Chat inspector transcript rendered"))
            .Select(e => e.Message)];

    private int OpenChats(params int[] messageCounts)
    {
        _vm = CreateSut([.. messageCounts.Select(Chat)]);
        _view = new AssistantHistoryView { DataContext = _vm };
        Lay();

        _vm.RefreshCommand.Execute(null);
        _vm.SelectedChat = _vm.Chats[0];
        Lay();
        return 0;
    }

    private void Lay()
    {
        _view.Measure(new Size(1400, 700));
        _view.Arrange(new Rect(0, 0, 1400, 700));
        _view.UpdateLayout();
    }

    private AssistantHistoryViewModel CreateSut(SyncAssistantChat[] chats)
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
            _logger,
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
