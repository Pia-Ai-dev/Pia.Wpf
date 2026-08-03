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
    /// <para>
    /// <paramref name="autoApprovedAs"/> non-null means the gate already authorized this call, so the card is
    /// returned pre-resolved (Accepted, <see cref="ActionCardInfo.IsAutoApproved"/>) as the bypass render — and
    /// it is the DECISION, not a bool, because the card has to SAY which authority ran the call. It used to be
    /// <c>bool autoApproved</c>, and the resolved text was unconditionally
    /// <c>ActionCard_AutoApproved</c> ("you always allow {0}"), which told a user who had clicked
    /// "Allow this session" that a PERMANENT grant now existed — one they would then look for in Settings and
    /// not find, because the session tier writes nothing. Null on the prompted path, which genuinely has no
    /// decision yet: the human is about to make it.
    /// </para>
    /// <para>
    /// <paramref name="toolClass"/> is the AUTHORITATIVE class the gate already derived from the plugin ROUTE
    /// (Batch 04 D4). Pass it whenever it is known — it is what stops the card and the gate from disagreeing
    /// about whether a tool is external. When null the builder falls back to
    /// <c>ToolClassifier.ClassifyPresumedExternal</c>, i.e. the name-only guess the card has always made.
    /// </para></summary>
    ActionCardInfo Build(
        PluginToolCall pendingAction,
        bool detokenize,
        ToolGateDecision? autoApprovedAs = null,
        ToolClass? toolClass = null);

    /// <summary>The transient status line shown while a tool call is running.</summary>
    string ResolveStatusText(string toolName);

    /// <summary>The snackbar title shown after an accepted write action succeeds.</summary>
    string ResolveSuccessTitle(string pluginName);
}
