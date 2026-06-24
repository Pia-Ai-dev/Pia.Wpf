using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Turns a pending plugin write-action into the inline <see cref="ActionCardInfo"/>
/// the chat surfaces for user confirmation, and resolves the localized status /
/// success strings for tool calls. Centralizes the tool-name → UI-string mapping
/// (category, action verb, delete warnings, privacy-token detokenization) that
/// previously lived in AssistantViewModel.
/// </summary>
public interface IActionCardBuilder
{
    /// <summary>Builds the confirmation card. When <paramref name="detokenize"/>
    /// is true, privacy tokens in the summary/details are resolved for display.
    /// When <paramref name="autoApproved"/> is true the card is returned pre-resolved
    /// (Accepted, <see cref="ActionCardInfo.IsAutoApproved"/>) for the standing-grant bypass render.</summary>
    ActionCardInfo Build(PluginToolCall pendingAction, bool detokenize, bool autoApproved = false);

    /// <summary>The transient status line shown while a tool call is running.</summary>
    string ResolveStatusText(string toolName);

    /// <summary>The snackbar title shown after an accepted write action succeeds.</summary>
    string ResolveSuccessTitle(string pluginName);
}
