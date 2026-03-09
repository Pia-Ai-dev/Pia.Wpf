using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.AI;

namespace Pia.Models;

public partial class AssistantMessage : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

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

    public bool HasActionCards => ActionCards.Count > 0;

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

    public DateTime Timestamp { get; } = DateTime.Now;

    public AssistantMessage(ChatRole role, string content = "")
    {
        Role = role;
        Content = content;
        ActionCards.CollectionChanged += OnActionCardsChanged;
    }

    private void OnActionCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasActionCards));
    }

    public ChatMessage ToChatMessage() => new(Role, Content);
}
