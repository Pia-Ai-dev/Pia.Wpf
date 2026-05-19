using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.AI;

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

    public bool HasActionCards => ActionCards.Count > 0;

    public bool HasSources => Sources.Count > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool HasContent => !string.IsNullOrEmpty(Content);

    public bool HasThinkingContent => !string.IsNullOrEmpty(ThinkingContent);

    public bool IsUser => Role == ChatRole.User;

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasContent));
    }

    partial void OnThinkingContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinkingContent));
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

    public ChatMessage ToChatMessage() => new(Role, Content);
}
