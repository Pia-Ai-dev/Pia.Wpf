using Pia.Models;

namespace Pia.ViewModels;

/// <summary>
/// Shared action-needed-first ordering for <see cref="ChatState"/>. Single source of
/// truth so adding/reordering a state only touches one place. Used to order the history
/// state-filter dropdown (WaitingForTool -> Running -> Error -> Completed -> Idle).
/// </summary>
internal static class ChatStateGrouping
{
    public static int StateGroupOrder(ChatState state) => state switch
    {
        ChatState.WaitingForTool => 0,
        ChatState.Running => 1,
        ChatState.Error => 2,
        ChatState.Completed => 3,
        ChatState.Idle => 4,
        _ => 5,
    };
}
