using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Converters;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Tests.Services;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>Utterances are fed through the internal AddUtterance hook, which keeps them deterministic.</summary>
public class DirectTranscriptionViewModelTests
{
    [Fact]
    public void SpeakerRegistered_AddsAMutedChip()
    {
        var (vm, service) = CreateSut();

        service.RaiseSpeakerRegistered("Speaker 2");

        var chip = Assert.Single(vm.ConsentChips);
        Assert.Equal("Speaker 2", chip.SpeakerLabel);
        Assert.Equal("Speaker 2", chip.DisplayName);
        Assert.False(chip.IsConsented);
        Assert.NotEmpty(chip.StatusText);
    }

    [Fact]
    public void SpeakerConsentChanged_Granted_MarksTheChipConsented_AndRelabelsBubbles()
    {
        var (vm, service) = CreateSut();
        service.RaiseSpeakerRegistered("Speaker 2");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello there", DateTimeOffset.Now, "Speaker 2"));
        Assert.Single(vm.Bubbles);

        // The rename succeeded, so the service reports the NEW label as the authoritative key and the old
        // one as OriginalSpeakerLabel — exactly what ConsentForwardLoop raises in that case.
        service.RaiseConsentChanged(
            "Alice", ConsentState.Unknown, ConsentState.Granted, "Alice", originalSpeakerLabel: "Speaker 2");

        var chip = Assert.Single(vm.ConsentChips);
        Assert.True(chip.IsConsented);
        Assert.Equal("Alice", chip.SpeakerLabel);
        Assert.Equal("Alice", chip.DisplayName);

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Alice", bubble.SpeakerLabel);
    }

    [Fact]
    public void SpeakerConsentChanged_Granted_WhenTheRenameWasRefused_KeepsTheConsentMapKeyOnTheChip()
    {
        // Revoke is issued with the chip's key, so a chip keyed by the reported name rather than by the
        // consent-map label would revoke a different speaker's entry — or nothing at all.
        var (vm, service) = CreateSut();
        service.RaiseSpeakerRegistered("Speaker 2");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello there", DateTimeOffset.Now, "Speaker 2"));

        service.RaiseConsentChanged(
            "Speaker 2", ConsentState.Unknown, ConsentState.Granted, "Alice", originalSpeakerLabel: "Speaker 2");

        var chip = Assert.Single(vm.ConsentChips);
        Assert.True(chip.IsConsented);
        Assert.Equal("Speaker 2", chip.SpeakerLabel);   // the consent-map key
        Assert.Equal("Alice", chip.DisplayName);        // the name is display text only

        // The bubble keeps the label the emitted utterance actually carries.
        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 2", bubble.SpeakerLabel);

        // And a revoke driven from that chip reaches the service under the key the consent map really has.
        vm.RevokeSpeakerCommand.Execute(chip.SpeakerLabel);
        Assert.Contains("Speaker 2", service.Revocations);
    }

    [Fact]
    public void ConsentSessionReset_ClearsChipsAndStats_ButKeepsAlreadyEmittedBubbles()
    {
        // A re-prepare builds a BRAND-NEW diarizer, so the old consent map is discarded — its "Speaker 1" is a
        // different voice now. The bubbles stay: that text was emitted lawfully under the consent of the time.
        var (vm, service) = CreateSut();
        service.RaiseSpeakerRegistered("Speaker 2");
        service.RaiseConsentChanged(
            "Alice", ConsentState.Unknown, ConsentState.Granted, "Alice", originalSpeakerLabel: "Speaker 2");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "already consented text", DateTimeOffset.Now, "Alice"));
        service.SetVoiceStats(new[] { new SpeakerVoiceStats(TranscriptSpeaker.Them, "Alice", 1, 5.0, 5.0, 1.0) });
        vm.ToggleStatsCommand.Execute(null);
        Assert.NotEmpty(vm.ConsentChips);
        Assert.NotEmpty(vm.VoiceStats);

        service.RaiseConsentSessionReset();

        Assert.Empty(vm.ConsentChips);
        Assert.Empty(vm.VoiceStats);
        Assert.Single(vm.Bubbles);
    }

    [Fact]
    public void MicSpeakingStarted_DoesNotMaterializeAnEmptyBubble()
    {
        // Voice activity that never produces transcribable text used to leave an EMPTY "me" bubble behind
        // forever; activity is reported through IsMicListening instead.
        var (vm, service) = CreateSut();

        service.RaiseSpeaking(TranscriptSpeaker.You, true);

        Assert.True(vm.IsMicListening);
        Assert.Empty(vm.Bubbles);
        Assert.False(vm.SummarizeWithAssistantCommand.CanExecute(null));

        service.RaiseSpeaking(TranscriptSpeaker.You, false);
        Assert.False(vm.IsMicListening);
        Assert.Empty(vm.Bubbles);
    }

    [Fact]
    public void MicSpeakingStarted_MarksAnExistingMeBubbleAsListening()
    {
        var (vm, service) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", DateTimeOffset.Now));

        service.RaiseSpeaking(TranscriptSpeaker.You, true);

        var bubble = Assert.Single(vm.Bubbles);
        Assert.True(bubble.IsListening);

        service.RaiseSpeaking(TranscriptSpeaker.You, false);
        Assert.False(bubble.IsListening);
    }

    [Fact]
    public async Task RenameRefusedByTheService_LeavesTheChipAndTranscriptUntouched()
    {
        // The service refuses a rename whose target label is already taken. The UI must not relabel
        // anything in that case, or the chip's key would diverge from the consent map's key.
        var (vm, service, dialog) = CreateSutWithDialog();
        service.RenameSucceeds = false;
        dialog.ShowInputDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult<string?>("Alice"));
        service.RaiseSpeakerRegistered("Speaker 2");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello there", DateTimeOffset.Now, "Speaker 2"));

        await vm.RenameSpeakerLabelCommand.ExecuteAsync("Speaker 2");

        Assert.Contains(("Speaker 2", "Alice"), service.Renames); // non-vacuity: the attempt really happened
        Assert.Equal("Speaker 2", Assert.Single(vm.ConsentChips).SpeakerLabel);
        Assert.Equal("Speaker 2", Assert.Single(vm.Bubbles).SpeakerLabel);
    }

    [Fact]
    public async Task RenameAcceptedByTheService_RelabelsTheChipAndTheTranscript()
    {
        var (vm, service, dialog) = CreateSutWithDialog();
        dialog.ShowInputDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult<string?>("Alice"));
        service.RaiseSpeakerRegistered("Speaker 2");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello there", DateTimeOffset.Now, "Speaker 2"));

        await vm.RenameSpeakerLabelCommand.ExecuteAsync("Speaker 2");

        Assert.Equal("Alice", Assert.Single(vm.ConsentChips).SpeakerLabel);
        Assert.Equal("Alice", Assert.Single(vm.Bubbles).SpeakerLabel);
    }

    [Fact]
    public void Revoke_RemovesThatSpeakersBubblesAndJournalEntries_AndLeavesOthersIntact()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "from two", DateTimeOffset.Now, "Speaker 2"));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "from three", DateTimeOffset.Now, "Speaker 3"));
        Assert.Equal(2, vm.Bubbles.Count);

        vm.RevokeSpeakerCommand.Execute("Speaker 2");

        var remaining = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 3", remaining.SpeakerLabel);

        // The journal is private, but its removal is observable: a new utterance for the same label starts a
        // brand-new bubble instead of merging into what the old entry would have produced.
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "from two again", DateTimeOffset.Now, "Speaker 2"));
        Assert.Equal(2, vm.Bubbles.Count);
    }

    [Fact]
    public void Revoke_DoesNotRemoveMicBubbles()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "mic text", DateTimeOffset.Now));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "them text", DateTimeOffset.Now, "Speaker 2"));
        Assert.Equal(2, vm.Bubbles.Count);

        vm.RevokeSpeakerCommand.Execute("Speaker 2");

        var remaining = Assert.Single(vm.Bubbles);
        Assert.Equal(TranscriptSpeaker.You, remaining.Speaker);
    }

    [Fact]
    public void MicUtterance_RendersAsTheLocalizedMeLabel()
    {
        var (vm, _) = CreateSut();

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", DateTimeOffset.Now));

        var bubble = Assert.Single(vm.Bubbles);
        var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel, vm.CounterpartName);
        // Not a hardcoded "you"/"me" literal: whatever LocalizationSource resolves for Speaker_Me today.
        Assert.Equal(LocalizationSource.Instance["Speaker_Me"], label);
    }

    [Fact]
    public void BuildMarkdown_ContainsTheFrontMatterSchemaAndTheStatsBlock()
    {
        var (vm, service) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", DateTimeOffset.Now));
        service.SetVoiceStats(new[]
        {
            new SpeakerVoiceStats(TranscriptSpeaker.You, null, 3, 42.5, 14.1667, 1.0),
        });

        var markdown = vm.BuildMarkdown();

        Assert.NotEmpty(markdown);
        Assert.Contains(DirectTranscriptMarkdown.Schema, markdown);
        // Distinctive duration value planted above; a stats block must surface it somewhere.
        Assert.Contains("42.5", markdown);
    }

    [Fact]
    public void BuildSummaryPrompt_ContainsNoYamlFrontMatter()
    {
        var (vm, _) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "agenda item one", DateTimeOffset.Now));

        string? captured = null;
        vm.SummarizeRequested += (_, prompt) => captured = prompt;

        vm.SummarizeWithAssistantCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Contains("agenda item one", captured);
        Assert.DoesNotContain(DirectTranscriptMarkdown.Schema, captured);
        Assert.DoesNotContain("---", captured);
    }

    [Fact]
    public void CanStart_RequiresDisclaimerAccepted()
    {
        var (vm, _) = CreateSut();

        Assert.False(vm.StartCommand.CanExecute(null));

        vm.DisclaimerAccepted = true;

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_ClearsListeningFlags_AndKeepsBubbles()
    {
        var (vm, service) = CreateSut();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", DateTimeOffset.Now));
        vm.Bubbles[0].IsListening = true;

        await vm.StopAsync();

        var remaining = Assert.Single(vm.Bubbles);
        Assert.False(remaining.IsListening);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task Resume_DoesNotClearTheTranscript()
    {
        var (vm, service) = CreateSut();
        vm.DisclaimerAccepted = true;
        await vm.StartCommand.ExecuteAsync(null);
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "hello", DateTimeOffset.Now));
        Assert.Single(vm.Bubbles);

        await vm.StopAsync();
        await vm.ResumeCommand.ExecuteAsync(null);

        Assert.Single(vm.Bubbles);
        Assert.Equal(2, service.StartCount);
        Assert.Equal(0, service.EndSessionCount);
    }

    [Fact]
    public async Task Start_AfterEndSession_ClearsTheTranscript()
    {
        var (vm, service) = CreateSut();
        vm.DisclaimerAccepted = true;
        await vm.StartCommand.ExecuteAsync(null);
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "first session", DateTimeOffset.Now));
        Assert.Single(vm.Bubbles);

        await vm.StopAsync();

        // Host reopens the overlay: PrepareForDisplayAsync ends the still-Prepared session (consent must
        // not survive a full close/reopen) and resets local UI state.
        await vm.PrepareForDisplayAsync();
        Assert.Equal(1, service.EndSessionCount);
        Assert.Empty(vm.Bubbles);

        // A stray utterance landing between reopen and the user clicking Start again — isolates
        // StartAsync's OWN "!_sessionStarted -> clear" branch from PrepareForDisplayAsync's clear.
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "stray", DateTimeOffset.Now));
        Assert.Single(vm.Bubbles);

        vm.DisclaimerAccepted = true;
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(vm.Bubbles);
    }

    [Fact]
    public void ToggleStats_FillsVoiceStatsFromTheService()
    {
        var (vm, service) = CreateSut();
        service.SetVoiceStats(new[]
        {
            new SpeakerVoiceStats(TranscriptSpeaker.Them, "Speaker 2", 4, 20.0, 5.0, 1.0),
        });

        vm.ToggleStatsCommand.Execute(null);

        Assert.True(vm.AreStatsVisible);
        var stat = Assert.Single(vm.VoiceStats);
        Assert.Equal("Speaker 2", stat.SpeakerLabel);

        vm.ToggleStatsCommand.Execute(null);

        Assert.False(vm.AreStatsVisible);
    }

    [Fact]
    public void Dispose_UnsubscribesEveryServiceEvent()
    {
        var (vm, service) = CreateSut();
        // One chip established BEFORE dispose, so the ConsentSessionReset assertion below is non-vacuous:
        // a still-attached handler would clear it.
        service.RaiseSpeakerRegistered("Speaker 1");
        Assert.Single(vm.ConsentChips);

        vm.Dispose();

        service.RaiseSpeakerRegistered("Speaker 9");
        Assert.Single(vm.ConsentChips);

        service.RaiseConsentChanged("Speaker 9", ConsentState.Unknown, ConsentState.Granted, "Nine");
        Assert.Single(vm.ConsentChips);
        Assert.False(Assert.Single(vm.ConsentChips).IsConsented);

        service.SetState(DirectTranscriptionState.Running);
        Assert.False(vm.IsRunning);

        service.RaiseSpeaking(TranscriptSpeaker.You, true);
        Assert.False(vm.IsMicListening);
        Assert.Empty(vm.Bubbles);

        service.RaiseConsentSessionReset();
        Assert.Single(vm.ConsentChips);
    }

    private static (DirectTranscriptionViewModel vm, FakeDirectTranscriptionService service) CreateSut()
    {
        var (vm, service, _) = CreateSutWithDialog();
        return (vm, service);
    }

    private static (DirectTranscriptionViewModel vm, FakeDirectTranscriptionService service, IDialogService dialog)
        CreateSutWithDialog()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());

        // Echo the key back as its own value so status/title assertions can match by key without a real resx.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => ci.Arg<string>());

        var files = Substitute.For<IFileDialogService>();
        var dialog = Substitute.For<IDialogService>();
        var service = new FakeDirectTranscriptionService();

        var vm = new DirectTranscriptionViewModel(
            service, settingsService, loc, files, dialog,
            NullLogger<DirectTranscriptionViewModel>.Instance, new InlineUiDispatcher());

        return (vm, service, dialog);
    }

    /// <summary>Every "raise" method fires synchronously on the calling thread, so no test needs polling.</summary>
    private sealed class FakeDirectTranscriptionService : IDirectTranscriptionService
    {
        private readonly Channel<TranscriptUtterance> _channel = Channel.CreateBounded<TranscriptUtterance>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        private readonly List<SpeakerVoiceStats> _voiceStats = [];

        public DirectTranscriptionState State { get; private set; } = DirectTranscriptionState.Idle;
        public ChannelReader<TranscriptUtterance> Utterances => _channel.Reader;

        public event EventHandler<DirectTranscriptionState>? StateChanged;
        public event EventHandler<SpeakerConsentChangedEventArgs>? SpeakerConsentChanged;
        public event EventHandler<string>? SpeakerRegistered;
        public event EventHandler<TranscriptionSpeakingChangedEventArgs>? SpeakingChanged;
        public event EventHandler? ConsentSessionReset;

        public int PrepareCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int EndSessionCount { get; private set; }
        public List<(string Old, string New)> Renames { get; } = [];
        public List<string> Revocations { get; } = [];

        public void SetVoiceStats(IEnumerable<SpeakerVoiceStats> stats)
        {
            _voiceStats.Clear();
            _voiceStats.AddRange(stats);
        }

        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            SetState(DirectTranscriptionState.Prepared);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            SetState(DirectTranscriptionState.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            SetState(DirectTranscriptionState.Prepared);
            return Task.CompletedTask;
        }

        public Task EndSessionAsync(CancellationToken cancellationToken = default)
        {
            EndSessionCount++;
            SetState(DirectTranscriptionState.Idle);
            return Task.CompletedTask;
        }

        /// <summary>Set false to model the service refusing a rename (a label collision).</summary>
        public bool RenameSucceeds { get; set; } = true;

        public bool RenameSpeaker(string oldLabel, string newLabel)
        {
            Renames.Add((oldLabel, newLabel));
            return RenameSucceeds;
        }

        public void RevokeSpeaker(string speakerLabel)
        {
            Revocations.Add(speakerLabel);
            RaiseConsentChanged(speakerLabel, ConsentState.Granted, ConsentState.Revoked, null);
        }

        public IReadOnlyList<SpeakerVoiceStats> GetVoiceStats() => _voiceStats.ToList();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // ---- test-only helpers to drive the view model ----

        public void SetState(DirectTranscriptionState newState)
        {
            State = newState;
            StateChanged?.Invoke(this, newState);
        }

        public void RaiseSpeakerRegistered(string speakerLabel) => SpeakerRegistered?.Invoke(this, speakerLabel);

        public void RaiseConsentChanged(
            string speakerLabel,
            ConsentState oldState,
            ConsentState newState,
            string? extractedName,
            string? originalSpeakerLabel = null)
            => SpeakerConsentChanged?.Invoke(this, new SpeakerConsentChangedEventArgs(
                speakerLabel, oldState, newState, extractedName, originalSpeakerLabel));

        public void RaiseConsentSessionReset() => ConsentSessionReset?.Invoke(this, EventArgs.Empty);

        public void RaiseSpeaking(TranscriptSpeaker speaker, bool isSpeaking)
            => SpeakingChanged?.Invoke(this, new TranscriptionSpeakingChangedEventArgs(speaker, isSpeaking));
    }
}
