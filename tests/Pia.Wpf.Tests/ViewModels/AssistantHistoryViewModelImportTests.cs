using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using System.Threading;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Import is a bulk writer, and the history view was built for single-chat edits. These cover the two
/// places that assumption breaks: one reload per imported chat, and a date filter that hides the result.
/// </summary>
public class AssistantHistoryViewModelImportTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    /// <summary>
    /// Runs posted callbacks inline, so a burst of <c>ChatsChanged</c> events is serialized the way the
    /// real dispatcher serializes it. The default context hands them to the thread pool, which would
    /// race on the debounce token and make the assertion flaky.
    /// </summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private AssistantHistoryViewModel CreateSut()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

        _chatService.SearchAsync().ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<SyncAssistantChat>>([]));
        _providers.GetProvidersAsync().Returns(Task.FromResult<IReadOnlyList<AiProvider>>([]));
        // Echo the key so a status assertion names the resource it expects.
        _localization[Arg.Any<string>()].Returns(call => (string)call[0]!);
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => $"{call[0]}({string.Join(",", (object[])call[1]!)})");

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService,
            _providers,
            Substitute.For<IDialogService>(),
            _localization,
            Substitute.For<INavigationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<IChatSessionManager>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IChatArchiveService>());
    }

    private void RaiseChatsChanged(int times)
    {
        for (var i = 0; i < times; i++)
        {
            _chatService.ChatsChanged += Raise.EventWith(
                _chatService,
                new AssistantChatChangedEventArgs { Id = Guid.NewGuid(), Kind = AssistantChatChangeKind.Upserted });
        }
    }

    /// <summary>
    /// An import saves each chat individually, so every one raises <c>ChatsChanged</c>. Reloading per
    /// event puts hundreds of searches on the UI thread, all contending for the same write gate the
    /// import's next save needs.
    /// </summary>
    [Fact]
    public async Task ABurstOfChatsChanged_CoalescesIntoOneReload()
    {
        var sut = CreateSut();
        _chatService.ClearReceivedCalls();

        RaiseChatsChanged(50);
        await Task.Delay(700, TestContext.Current.CancellationToken);

        var reloads = _chatService.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IAssistantChatService.SearchAsync));
        Assert.Equal(1, reloads);
        sut.Dispose();
    }

    /// <summary>
    /// History opens on the last 30 days. A migration's chats are older than that, so without widening
    /// the filter the import writes hundreds of rows and the list shows none of them.
    /// </summary>
    [Fact]
    public async Task RevealingAnImport_WidensADateFilterThatWouldHideIt()
    {
        var sut = CreateSut();
        var oldest = DateTime.UtcNow.AddDays(-400);

        sut.FilterStartDate = DateTime.Today.AddDays(-30);
        sut.FilterEndDate = DateTime.Today.AddDays(-2);
        sut.SearchQuery = "leftover query";
        sut.SelectedProviderId = Guid.NewGuid();

        await sut.RevealImportedChatsAsync(new ChatImportResult
        {
            Format = ChatArchiveFormat.OpenWebUi,
            Imported = 573,
            OldestUpdatedAt = oldest,
        });

        Assert.Equal(oldest.ToLocalTime().Date, sut.FilterStartDate);
        Assert.Equal(DateTime.Today, sut.FilterEndDate);
        Assert.Equal(string.Empty, sut.SearchQuery);
        Assert.Null(sut.SelectedProviderId);
        Assert.Same(sut.StateFilterOptions[0], sut.SelectedStateOption);
        sut.Dispose();
    }

    /// <summary>
    /// History only ever queried one page of 50 with no way to reach the rest. Nobody had more than 50
    /// chats before imports existed; a 573-chat migration reads as data loss.
    /// </summary>
    [Fact]
    public async Task LoadMore_AppendsTheNextPage_AndStopsAtTheTotal()
    {
        var all = Enumerable.Range(0, 120)
            .Select(i => new SyncAssistantChat
            {
                Id = Guid.NewGuid(),
                Title = $"chat {i}",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-i),
                Messages = [new SyncAssistantChatMessage { Id = Guid.NewGuid(), Content = "x" }],
            })
            .ToList();

        var sut = CreateSut();

        _chatService.CountAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(all.Count);
        _chatService.SearchAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var offset = (int)call[4]!;
            var limit = (int)call[5]!;
            return Task.FromResult<IReadOnlyList<SyncAssistantChat>>(all.Skip(offset).Take(limit).ToList());
        });
        await sut.OnNavigatedToAsync(null);

        Assert.Equal(50, sut.Chats.Count);
        Assert.Equal(120, sut.TotalCount);
        Assert.True(sut.HasMoreChats);

        await sut.LoadMoreChatsCommand.ExecuteAsync(null);
        Assert.Equal(100, sut.Chats.Count);
        Assert.True(sut.HasMoreChats);

        await sut.LoadMoreChatsCommand.ExecuteAsync(null);
        Assert.Equal(120, sut.Chats.Count);
        Assert.False(sut.HasMoreChats);

        // A filter change must restart at page one rather than appending to a stale list.
        sut.SearchQuery = "chat";
        await sut.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(50, sut.Chats.Count);
        sut.Dispose();
    }

    /// <summary>
    /// Only the storing phase knows how many chats there are, so it is the only one that can drive a
    /// determinate bar — the other two would otherwise sit at 0% and read as a hang.
    /// </summary>
    [Fact]
    public void ImportProgress_IsIndeterminate_UntilTheChatCountIsKnown()
    {
        var sut = CreateSut();

        sut.BeginImportProgress();
        Assert.True(sut.IsImporting);
        Assert.True(sut.ImportProgressIsIndeterminate);
        Assert.Equal("Msg_AssistantHistory_ImportReading", sut.ImportStatus);

        sut.ReportImportProgress(new ChatImportProgress(ChatImportPhase.Converting, 0, 0));
        Assert.True(sut.ImportProgressIsIndeterminate);
        Assert.Equal("Msg_AssistantHistory_ImportConverting", sut.ImportStatus);

        sut.ReportImportProgress(new ChatImportProgress(ChatImportPhase.Storing, 143, 573));
        Assert.False(sut.ImportProgressIsIndeterminate);
        Assert.Equal(143d * 100 / 573, sut.ImportProgress);
        Assert.Equal("Msg_AssistantHistory_ImportStoring(143,573)", sut.ImportStatus);

        sut.Dispose();
    }

    /// <summary>
    /// An import's own events must not reload at all: it reveals the whole result itself when it is
    /// done, and every search it triggers in the meantime fights the write gate the next save needs.
    /// </summary>
    [Fact]
    public async Task ChatsChangedDuringAnImport_TriggersNoReload()
    {
        var sut = CreateSut();
        sut.BeginImportProgress();
        _chatService.ClearReceivedCalls();

        RaiseChatsChanged(50);
        await Task.Delay(700, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            _chatService.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IAssistantChatService.SearchAsync));
        sut.Dispose();
    }

    [Fact]
    public async Task RevealingAnImport_LeavesAnAlreadyWiderFilterAlone()
    {
        var sut = CreateSut();
        var wider = DateTime.Today.AddYears(-5);
        sut.FilterStartDate = wider;

        await sut.RevealImportedChatsAsync(new ChatImportResult
        {
            Format = ChatArchiveFormat.Pia,
            Imported = 1,
            OldestUpdatedAt = DateTime.UtcNow.AddDays(-10),
        });

        Assert.Equal(wider, sut.FilterStartDate);
        sut.Dispose();
    }
}
