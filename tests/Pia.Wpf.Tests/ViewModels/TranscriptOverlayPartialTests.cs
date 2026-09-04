using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Tests.Services;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The overlay rebuilds its bubbles from the journal on every speaker reassignment. A partial that
/// reached a bubble or the journal would be duplicated or dropped by that rebuild, so the whole
/// point of these tests is that it reaches neither.
/// </summary>
public class TranscriptOverlayPartialTests
{
    private sealed class TestOverlay : TranscriptOverlayViewModel
    {
        private readonly Channel<TranscriptUtterance> _channel = Channel.CreateUnbounded<TranscriptUtterance>();

        public TestOverlay() : base(
            Substitute.For<ISettingsService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger.Instance,
            new InlineUiDispatcher(),
            chatSessionManager: null,
            workingDirectoryService: null)
        { }

        protected override ChannelReader<TranscriptUtterance> UtteranceReader => _channel.Reader;
        protected override string TitleKey => "Test_Title";
        protected override string SaveDialogTitleKey => "Test_SaveTitle";
        protected override string SaveDialogFilterKey => "Test_SaveFilter";
        protected override string SaveFileNamePrefix => "test";
        protected override string MeetingSourceKind => "test";
    }

    [Fact]
    public void A_partial_never_enters_the_bubbles_or_the_journal()
    {
        var vm = new TestOverlay();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "committed", DateTimeOffset.Now));
        vm.SetPartial(TranscriptSpeaker.You, "still being said");

        Assert.Equal("still being said", vm.PartialText);
        Assert.Single(vm.Bubbles);
        Assert.Equal("committed", vm.Bubbles[0].Text);
    }

    [Fact]
    public void A_final_utterance_clears_the_partial_it_replaces()
    {
        var vm = new TestOverlay();
        vm.SetPartial(TranscriptSpeaker.You, "still being said");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "still being said", DateTimeOffset.Now));

        Assert.Equal(string.Empty, vm.PartialText);
        Assert.Single(vm.Bubbles);
        Assert.Equal("still being said", vm.Bubbles[0].Text);
    }

    /// <summary>
    /// RebuildBubblesFromJournal is the operation the whole separation exists for: it replays only
    /// the journal, so a partial that had leaked into either would duplicate here or vanish.
    /// </summary>
    [Fact]
    public void A_reassignment_rebuild_neither_duplicates_nor_swallows_the_partial()
    {
        var vm = new TestOverlay();
        vm.AddUtterance(new TranscriptUtterance(
            TranscriptSpeaker.Them, "committed", DateTimeOffset.Now, SpeakerLabel: "Speaker 1", SegmentId: 7));
        vm.SetPartial(TranscriptSpeaker.Them, "still being said");

        vm.ApplyReassignments([new SpeakerReassignment(7, "Speaker 2")]);

        Assert.Equal("still being said", vm.PartialText);
        Assert.Single(vm.Bubbles);
        Assert.Equal("committed", vm.Bubbles[0].Text);
        Assert.Equal("Speaker 2", vm.Bubbles[0].SpeakerLabel);
    }
}
