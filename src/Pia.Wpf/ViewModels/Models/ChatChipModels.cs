namespace Pia.ViewModels.Models;

/// <summary>
/// A date-grouped bucket of recent chats shown in the chat-title flyout.
/// Plain data holder (no change notification) — lives in ViewModels.Models.
/// </summary>
public sealed class ChatChipGroupViewModel
{
    public required string DisplayName { get; init; }
    public required IReadOnlyList<ChatChipItemViewModel> Items { get; init; }
}

/// <summary>A single chat entry within a <see cref="ChatChipGroupViewModel"/>.</summary>
public sealed record ChatChipItemViewModel(Guid Id, string Title);
