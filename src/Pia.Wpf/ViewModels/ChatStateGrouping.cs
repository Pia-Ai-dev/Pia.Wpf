using Pia.Models;

namespace Pia.ViewModels;

/// <summary>
/// Shared state-grouping ordering + localization-key mapping for the "group by state"
/// views (history list + chip flyout). Single source of truth so adding/reordering a
/// <see cref="ChatState"/> only touches one place. Presentation altitude (returns loc
/// keys), so it lives in the ViewModels layer, not on the domain enum.
/// Bucket order is action-needed first: WaitingForTool -> Running -> Error -> Completed -> Idle.
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

    public static string StateGroupResourceKey(ChatState state) => state switch
    {
        ChatState.Running => "ChatState_Group_Running",
        ChatState.WaitingForTool => "ChatState_Group_WaitingForTool",
        ChatState.Completed => "ChatState_Group_Completed",
        ChatState.Error => "ChatState_Group_Error",
        _ => "ChatState_Group_Idle",
    };
}
