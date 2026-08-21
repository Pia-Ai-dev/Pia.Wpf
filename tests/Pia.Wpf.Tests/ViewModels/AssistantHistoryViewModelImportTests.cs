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

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService,
            _providers,
            Substitute.For<IDialogService>(),
            Substitute.For<ILocalizationService>(),
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
