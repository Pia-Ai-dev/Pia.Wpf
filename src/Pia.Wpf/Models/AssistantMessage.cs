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

    /// <summary>Localized "Thought for Ns" label, set when the thinking phase ends.</summary>
    [ObservableProperty]
    private string _reasoningDurationLabel = string.Empty;

    public ObservableCollection<ActionCardInfo> ActionCards { get; } = [];

    public ObservableCollection<SourceRef> Sources { get; } = [];

    /// <summary>Local files this turn read, wrote, exported, or referenced via @File. Rendered as
    /// open-file/open-folder chips (PiaFileChip). In-memory only — not persisted (see Sources).</summary>
    public ObservableCollection<FileRef> FileRefs { get; } = [];

    public ObservableCollection<string> Suggestions { get; } = [];

    /// <summary>Typed "switch to Agent mode" chips (R8) — net-new, not the string <see cref="Suggestions"/>.
    /// Populated pre-route in <c>ChatSession.HandleToolCall</c> when the model calls suggest_agent_mode.</summary>
    public ObservableCollection<AgentModeSuggestion> AgentModeSuggestions { get; } = [];

    [ObservableProperty]
    private MessageMeta? _meta;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRateable))]
    private AnswerStats? _stats;

    /// <summary>Only Pia Cloud answers can be rated or reported — a BYOK model is the user's own, not ours.</summary>
    public bool IsRateable => Stats?.IsPiaCloud == true;

    /// <summary>True when the server routed this answer to the protected/private model (guardrail
    /// HIT/ERROR). Drives the neutral shield indicator. In-memory only — not persisted (v1).</summary>
    [ObservableProperty]
    private bool _isProtectedRoute;

    /// <summary>True when the in-step tool loop ran out of rounds before the model stopped calling
    /// tools on its own — read by <c>ChatSession.CleanupPerExchange</c> to pick the right empty-response
    /// wording if even the tools-disabled wrap-up call came back with no text. In-memory only.</summary>
    [ObservableProperty]
    private bool _toolRoundsExhausted;

    /// <summary>Tool calls made so far this turn. Incremented by <c>ChatSession.HandleToolCallWithStatus</c>
    /// once per call, live while streaming. In-memory only.</summary>
    [ObservableProperty]
    private int _toolCallCount;

    /// <summary>Localized "Tool calls: N" display text, refreshed alongside <see cref="ToolCallCount"/>.</summary>
    [ObservableProperty]
    private string _toolCallCountLabel = string.Empty;

    [ObservableProperty]
    private PersonaAttribution? _persona;

    [ObservableProperty]
    private ImageAttachment? _attachment;

    /// <summary>Rendered text of the files attached to this message, appended to the AI-visible
    /// message but never displayed. In-memory only — not persisted (see AssistantMessageMapper).</summary>
    [ObservableProperty]
    private string? _attachedFileContext;

    public bool HasActionCards => ActionCards.Count > 0;

    /// <summary>True while any inline action card is still awaiting a user decision.
    /// Drives the in-transcript "awaiting confirmation" accent. Computed (no backing
    /// field) — never persisted (see AssistantMessageMapper).</summary>
    public bool HasPendingConfirmation => ActionCards.Any(c => c.IsPending);

    public bool HasSources => Sources.Count > 0;

    public bool HasFileRefs => FileRefs.Count > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool HasAgentModeSuggestion => AgentModeSuggestions.Count > 0;

    public bool HasContent => !string.IsNullOrEmpty(Content);

    public bool HasThinkingContent => !string.IsNullOrEmpty(ThinkingContent);

    public bool HasReasoningDuration => !string.IsNullOrEmpty(ReasoningDurationLabel);

    /// <summary>The "thinking" phase — streaming with no answer text yet. Drives the live
    /// rolling-reasoning view (header shows <see cref="StatusText"/>, body shows the latest
    /// <see cref="ThinkingContent"/>).</summary>
    public bool ShowLiveReasoning => IsStreaming && !HasContent;

    /// <summary>The thinking phase is over and left a duration or a trace — drives the
    /// collapsed toggle shown above the answer.</summary>
    public bool ShowReasoningSummary => (HasThinkingContent || HasReasoningDuration) && !ShowLiveReasoning;

    public bool HasToolCalls => ToolCallCount > 0;

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
        OnPropertyChanged(nameof(ShowReasoningSummary));
    }

    partial void OnToolCallCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasToolCalls));
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
        AgentModeSuggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAgentModeSuggestion));
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

    /// <summary>
    /// Adds a source chip, deduplicating by target. Web sources append and keep the number the citation
    /// extractor gave them — it has to match the <c>[N]</c> marker in the text — so the unnumbered vault
    /// and chat chips, which are collected during the turn, sort ahead of them.
    /// </summary>
    public void AddSource(SourceRef incoming)
    {
        var key = KeyOf(incoming);
        if (!string.IsNullOrEmpty(key) &&
            Sources.Any(s => s.Kind == incoming.Kind &&
                             string.Equals(KeyOf(s), key, StringComparison.OrdinalIgnoreCase)))
            return;

        // Arrival order, not list position: an insert must not reuse an Ordinal the chip ids depend on.
        var chip = incoming with { Ordinal = Sources.Count == 0 ? 1 : Sources.Max(s => s.Ordinal) + 1 };

        if (chip.Kind == SourceRefKind.Web)
        {
            Sources.Add(chip);
            return;
        }

        var firstWeb = -1;
        for (var i = 0; i < Sources.Count; i++)
        {
            if (Sources[i].Kind != SourceRefKind.Web) continue;
            firstWeb = i;
            break;
        }

        if (firstWeb < 0)
            Sources.Add(chip);
        else
            Sources.Insert(firstWeb, chip);
    }

    private static string? KeyOf(SourceRef source) =>
        source.Kind == SourceRefKind.Web ? source.Url : source.Target;

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

    public ChatMessage ToChatMessage() => BuildChatMessage(Content);

    /// <summary>
    /// Builds the AI-visible message with <paramref name="overrideText"/> instead of <see cref="Content"/>
    /// (used to inject @Files context / regeneration instructions without changing the displayed bubble).
    /// </summary>
    public ChatMessage ToChatMessage(string overrideText) => BuildChatMessage(overrideText);

    // One builder for both overloads: attached file text has to ride the no-image path too, and an
    // image attachment has to survive an override.
    private ChatMessage BuildChatMessage(string text)
    {
        var context = AttachedFileContext;
        var visible = string.IsNullOrEmpty(context)
            ? text
            : string.IsNullOrEmpty(text) ? context : $"{text}\n\n{context}";

        if (Attachment is null) return new ChatMessage(Role, visible);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(visible))
        {
            contents.Add(new TextContent(visible));
        }
        contents.Add(new DataContent(Attachment.JpegBytes, Attachment.MimeType));
        return new ChatMessage(Role, contents);
    }
}
