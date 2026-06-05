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

    public ObservableCollection<ActionCardInfo> ActionCards { get; } = [];

    public ObservableCollection<SourceRef> Sources { get; } = [];

    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty]
    private MessageMeta? _meta;

    [ObservableProperty]
    private AnswerStats? _stats;

    [ObservableProperty]
    private PersonaAttribution? _persona;

    [ObservableProperty]
    private ImageAttachment? _attachment;

    public bool HasActionCards => ActionCards.Count > 0;

    public bool HasSources => Sources.Count > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool HasContent => !string.IsNullOrEmpty(Content);

    public bool HasThinkingContent => !string.IsNullOrEmpty(ThinkingContent);

    public bool HasAttachment => Attachment is not null;

    public bool IsUser => Role == ChatRole.User;

    public bool HasPersona => Persona is not null;

    /// <summary>Glyph id for the avatar: the snapshot's persona, or the Pia icon for legacy messages.</summary>
    public Guid PersonaGlyphId => Persona?.Id ?? BuiltInPersonas.PiaPersonalId;

    public string? PersonaGlyphEmoji => Persona?.Emoji;

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasContent));
    }

    partial void OnThinkingContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinkingContent));
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
        Suggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSuggestions));
    }

    private void OnActionCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasActionCards));
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
}
