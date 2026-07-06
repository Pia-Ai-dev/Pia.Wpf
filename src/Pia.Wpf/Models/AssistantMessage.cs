using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.AI;
using Pia.Shared;

namespace Pia.Models;

public partial class AssistantMessage : ObservableObject
{
    public Guid Id { get; }

    public ChatRole Role { get; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _thinkingContent = string.Empty;

    [ObservableProperty]
    private string _statusText = "Thinking...";

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isSpeaking;

    /// <summary>Whether the collapsed reasoning toggle is expanded to show the full reasoning.
    /// Per-message UI state (the message template is reused as the list virtualizes).</summary>
    [ObservableProperty]
    private bool _isReasoningExpanded;

    /// <summary>Localized "Thought for Ns" label, set once reasoning completes. Empty when the
    /// turn produced no reasoning.</summary>
    [ObservableProperty]
    private string _reasoningDurationLabel = string.Empty;

    public ObservableCollection<ActionCardInfo> ActionCards { get; } = [];

    public ObservableCollection<SourceRef> Sources { get; } = [];

    /// <summary>Local files this turn read, wrote, exported, or referenced via @File. Rendered as
    /// open-file/open-folder chips (PiaFileChip). In-memory only — not persisted (see Sources).</summary>
    public ObservableCollection<FileRef> FileRefs { get; } = [];

    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty]
    private MessageMeta? _meta;

    [ObservableProperty]
    private AnswerStats? _stats;

    /// <summary>True when the server routed this answer to the protected/private model (guardrail
    /// HIT/ERROR). Drives the neutral shield indicator. In-memory only — not persisted (v1).</summary>
    [ObservableProperty]
    private bool _isProtectedRoute;

    [ObservableProperty]
    private PersonaAttribution? _persona;

    [ObservableProperty]
    private ImageAttachment? _attachment;

    public bool HasActionCards => ActionCards.Count > 0;

    /// <summary>True while any inline action card is still awaiting a user decision.
    /// Drives the in-transcript "awaiting confirmation" accent. Computed (no backing
    /// field) — never persisted (see AssistantMessageMapper).</summary>
    public bool HasPendingConfirmation => ActionCards.Any(c => c.IsPending);

    public bool HasSources => Sources.Count > 0;

    public bool HasFileRefs => FileRefs.Count > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool HasContent => !string.IsNullOrEmpty(Content);

    public bool HasThinkingContent => !string.IsNullOrEmpty(ThinkingContent);

    public bool HasReasoningDuration => !string.IsNullOrEmpty(ReasoningDurationLabel);

    /// <summary>The "thinking" phase — streaming with no answer text yet. Drives the live
    /// rolling-reasoning view (header shows <see cref="StatusText"/>, body shows the latest
    /// <see cref="ThinkingContent"/>).</summary>
    public bool ShowLiveReasoning => IsStreaming && !HasContent;

    /// <summary>Reasoning exists and the thinking phase is over — drives the collapsed
    /// "Thought for Ns" toggle shown above the answer.</summary>
    public bool ShowReasoningSummary => HasThinkingContent && !ShowLiveReasoning;

    public bool HasAttachment => Attachment is not null;

    public bool IsUser => Role == ChatRole.User;

    public bool HasPersona => Persona is not null;

    /// <summary>Glyph id for the avatar: the snapshot's persona, or the Pia icon for legacy messages.</summary>
    public Guid PersonaGlyphId => Persona?.Id ?? BuiltInPersonas.PiaPersonalId;

    public string? PersonaGlyphEmoji => Persona?.Emoji;

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowLiveReasoning));
        OnPropertyChanged(nameof(ShowReasoningSummary));
    }

    partial void OnThinkingContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinkingContent));
        OnPropertyChanged(nameof(ShowReasoningSummary));
    }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLiveReasoning));
        OnPropertyChanged(nameof(ShowReasoningSummary));
    }

    partial void OnReasoningDurationLabelChanged(string value)
    {
        OnPropertyChanged(nameof(HasReasoningDuration));
    }

    partial void OnAttachmentChanged(ImageAttachment? value)
    {
        OnPropertyChanged(nameof(HasAttachment));
    }

    partial void OnPersonaChanged(PersonaAttribution? value)
    {
        OnPropertyChanged(nameof(HasPersona));
        OnPropertyChanged(nameof(PersonaGlyphId));
        OnPropertyChanged(nameof(PersonaGlyphEmoji));
    }

    public DateTime Timestamp { get; }

    public AssistantMessage(ChatRole role, string content = "")
        : this(Guid.NewGuid(), role, content, DateTime.Now)
    {
    }

    public AssistantMessage(Guid id, ChatRole role, string content, DateTime timestamp)
    {
        Id = id;
        Role = role;
        Content = content;
        Timestamp = timestamp;
        ActionCards.CollectionChanged += OnActionCardsChanged;
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSources));
        FileRefs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFileRefs));
        Suggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSuggestions));
    }

    /// <summary>
    /// Adds a file chip, deduplicating by absolute path (Windows-insensitive). If the file was
    /// already touched this turn, keeps the higher-precedence <see cref="FileRefKind"/> (e.g. a
    /// file Created then later Updated stays "Created"; an Exported output outranks all) so a
    /// single file never shows two chips. The shared entry point for the tool sink, the @File
    /// path, and HTML export.
    /// </summary>
    public void AddOrUpgradeFileRef(FileRef incoming)
    {
        for (var i = 0; i < FileRefs.Count; i++)
        {
            var existing = FileRefs[i];
            if (!string.Equals(existing.AbsolutePath, incoming.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (incoming.Kind > existing.Kind)
                FileRefs[i] = incoming;
            return;
        }
        FileRefs.Add(incoming);
    }

    private void OnActionCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ActionCards is get-only and only ever .Add'd (see ChatSession.HandleToolCall),
        // so the per-card detach below covers every reachable mutation. A Reset (Clear())
        // gives no OldItems and would leak the existing subscriptions — unsupported until
        // the detach is generalized (e.g. tracking the subscribed set).
        if (e.OldItems is not null)
            foreach (ActionCardInfo card in e.OldItems)
                card.PropertyChanged -= OnActionCardPropertyChanged;
        if (e.NewItems is not null)
            foreach (ActionCardInfo card in e.NewItems)
                card.PropertyChanged += OnActionCardPropertyChanged;

        OnPropertyChanged(nameof(HasActionCards));
        OnPropertyChanged(nameof(HasPendingConfirmation));
    }

    private void OnActionCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionCardInfo.IsPending))
            OnPropertyChanged(nameof(HasPendingConfirmation));
    }

    public ChatMessage ToChatMessage()
    {
        if (Attachment is null) return new ChatMessage(Role, Content);

        var contents = new List<AIContent>();
        if (HasContent)
        {
            contents.Add(new TextContent(Content));
        }
        contents.Add(new DataContent(Attachment.JpegBytes, Attachment.MimeType));
        return new ChatMessage(Role, contents);
    }

    /// <summary>
    /// Builds the AI-visible message with <paramref name="overrideText"/> instead of <see cref="Content"/>
    /// (used to inject @Files context / regeneration instructions without changing the displayed bubble),
    /// while preserving any image attachment. The image-encoding branch mirrors <see cref="ToChatMessage()"/>
    /// exactly — without this overload the prior text-only injection silently dropped the attachment.
    /// </summary>
    public ChatMessage ToChatMessage(string overrideText)
    {
        if (Attachment is null) return new ChatMessage(Role, overrideText);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(overrideText))
        {
            contents.Add(new TextContent(overrideText));
        }
        contents.Add(new DataContent(Attachment.JpegBytes, Attachment.MimeType));
        return new ChatMessage(Role, contents);
    }
}
