using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;
using Pia.Shared.Models;

namespace Pia.ViewModels.Models;

/// <summary>
/// A per-row wrapper over the immutable <see cref="SyncAssistantChat"/> DTO so a
/// history/quick-switcher row can carry a live, observable <see cref="ChatState"/>
/// without extending the Shared sync contract. The state is seeded from the
/// session manager at wrap time and refreshed in place on <c>SessionStateChanged</c>.
/// </summary>
public sealed partial class AssistantChatRowViewModel : ObservableObject
{
    public SyncAssistantChat Chat { get; }

    public Guid Id => Chat.Id;

    /// <summary>Proxied for the row XAML (so existing <c>{Binding Title}</c> shapes keep working via <c>Chat.Title</c>).</summary>
    public string? Title => Chat.Title;

    public DateTime UpdatedAt => Chat.UpdatedAt;

    [ObservableProperty]
    private ChatState _state;

    public AssistantChatRowViewModel(SyncAssistantChat chat, ChatState seed)
    {
        Chat = chat;
        _state = seed;
    }
}

/// <summary>An option in the history live-state filter: a <see cref="ChatState"/>, or
/// null for "All states".</summary>
public sealed record ChatStateFilterOption(ChatState? State, string DisplayName);
