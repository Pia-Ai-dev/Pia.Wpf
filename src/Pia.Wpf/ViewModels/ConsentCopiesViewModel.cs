using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Services.Consent;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>The copies of a transcript a speaker just withdrew from: exports to point at, notes and chats to delete.</summary>
public sealed partial class ConsentCopiesViewModel : ObservableObject
{
    private readonly IConsentCopyService _copies;
    private readonly ILogger<ConsentCopiesViewModel> _logger;
    private readonly IChatSessionManager? _chatSessionManager;

    // A kept note must not be rewritten while its deletion is still in flight, or the write would bring it back.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string[] _sessionIds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopies))]
    private bool _copiesLogUnreadable;

    [ObservableProperty]
    private bool _deleteFailed;

    public ObservableCollection<ConsentExportRow> Exports { get; } = [];

    public ObservableCollection<ConsentNoteRow> Notes { get; } = [];

    public ObservableCollection<ConsentChatRow> Chats { get; } = [];

    public bool HasExports => Exports.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasChats => Chats.Count > 0;

    public bool HasCopies => HasExports || HasNotes || HasChats || CopiesLogUnreadable;

    public ConsentCopiesViewModel(
        IConsentCopyService copies,
        ILogger<ConsentCopiesViewModel> logger,
        IChatSessionManager? chatSessionManager = null)
    {
        _copies = copies;
        _logger = logger;
        _chatSessionManager = chatSessionManager;

        Exports.CollectionChanged += (_, _) => OnCollectionChanged(nameof(HasExports));
        Notes.CollectionChanged += (_, _) => OnCollectionChanged(nameof(HasNotes));
        Chats.CollectionChanged += (_, _) => OnCollectionChanged(nameof(HasChats));
    }

    public async Task LoadAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
    {
        _sessionIds = sessionIds.ToArray();
        var inventory = await _copies.FindAsync(_sessionIds, cancellationToken);

        Exports.Clear();
        foreach (var export in inventory.Exports)
            Exports.Add(new ConsentExportRow(export.Path!, export.At.LocalDateTime));

        Notes.Clear();
        for (var i = 0; i < inventory.Notes.Count; i++)
            Notes.Add(new ConsentNoteRow(i + 1, inventory.Notes[i]));

        Chats.Clear();
        foreach (var chat in inventory.Chats)
            Chats.Add(new ConsentChatRow(chat.ChatId!.Value, chat.At.LocalDateTime));

        CopiesLogUnreadable = inventory.CopiesLogUnreadable;
    }

    /// <summary>Writes the revocation into every note the user kept.</summary>
    public async Task RecordRevocationInKeptNotesAsync(ConsentRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var recorded = 0;
            foreach (var note in Notes.ToList())
            {
                if (await _copies.RecordRevocationAsync(note.Reference, record, cancellationToken)) recorded++;
            }
            _logger.LogInformation("Kept vault notes of a revoked session: {Recorded} of {Count} carry the revocation",
                recorded, Notes.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    [RelayCommand]
    private Task DeleteNoteAsync(ConsentNoteRow? note) => DeleteNotesAsync(note is null ? [] : [note]);

    [RelayCommand]
    private Task DeleteAllNotesAsync() => DeleteNotesAsync(Notes.ToList());

    [RelayCommand]
    private Task DeleteChatAsync(ConsentChatRow? chat) => DeleteChatsAsync(chat is null ? [] : [chat]);

    [RelayCommand]
    private Task DeleteAllChatsAsync() => DeleteChatsAsync(Chats.ToList());

    private async Task DeleteNotesAsync(IReadOnlyList<ConsentNoteRow> notes)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var note in notes)
            {
                // A queued single delete can find its row already taken by a "delete all".
                if (!Notes.Contains(note)) continue;

                if (await _copies.DeleteNoteAsync(note.Reference, _sessionIds)) Notes.Remove(note);
                else DeleteFailed = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DeleteChatsAsync(IReadOnlyList<ConsentChatRow> chats)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var chat in chats)
            {
                if (!Chats.Contains(chat)) continue;

                if (await _copies.DeleteChatAsync(chat.ChatId))
                {
                    Chats.Remove(chat);
                    AbandonActiveSessionIfDeleted(chat.ChatId);
                }
                else
                {
                    DeleteFailed = true;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The summary chat is usually the open one, whose next turn would store it again.
    private void AbandonActiveSessionIfDeleted(Guid chatId)
    {
        var active = _chatSessionManager?.ActiveSession;
        if (active?.Id != chatId) return;

        var inheritedDirectory = active.WorkingDirectory;
        active.Cancel();
        _chatSessionManager!.GetOrCreateActiveForNewChat().SetWorkingDirectory(inheritedDirectory);
    }

    private void OnCollectionChanged(string hasProperty)
    {
        OnPropertyChanged(hasProperty);
        OnPropertyChanged(nameof(HasCopies));
    }
}
