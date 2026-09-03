using Pia.Models;

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

/// <summary>A single chat entry within a <see cref="ChatChipGroupViewModel"/>.
/// <paramref name="State"/> is a snapshot (read once at build time) driving the inline
/// row state badge; it does not update live while the flyout is open.</summary>
public sealed record ChatChipItemViewModel(Guid Id, string Title, DateTime UpdatedAt, ChatState State);

/// <summary>What a row's inline rename hands its host: the row it belongs to and the name typed into it.
/// The name cannot ride on <paramref name="Row"/> — the item VMs are immutable snapshots.</summary>
public sealed record ChatRowRenameRequest(object? Row, string Title);
