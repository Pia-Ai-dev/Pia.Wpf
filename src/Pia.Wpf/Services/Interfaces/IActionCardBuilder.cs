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
    /// is true, privacy tokens in the summary/details are resolved for display.</summary>
    ActionCardInfo Build(PluginToolCall pendingAction, bool detokenize);

    /// <summary>The transient status line shown while a tool call is running.</summary>
    string ResolveStatusText(string toolName);

    /// <summary>The snackbar title shown after an accepted write action succeeds.</summary>
    string ResolveSuccessTitle(string pluginName);
}
