using System.Collections.ObjectModel;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Default <see cref="IActionCardBuilder"/>. Maps a plugin's pending write-action
/// to a confirmation card and resolves the localized status/success strings,
/// applying privacy-token detokenization to user-facing values when enabled.
/// </summary>
public sealed class ActionCardBuilder : IActionCardBuilder
{
    private readonly ILocalizationService _localizationService;
    private readonly ITokenMapService _tokenMapService;

    public ActionCardBuilder(
        ILocalizationService localizationService,
        ITokenMapService tokenMapService)
    {
        _localizationService = localizationService;
        _tokenMapService = tokenMapService;
    }

    public ActionCardInfo Build(
        PluginToolCall pendingAction,
        bool detokenize,
        ToolGateDecision? autoApprovedAs = null,
        ToolClass? toolClass = null)
    {
        var autoApproved = autoApprovedAs is not null;
        // ONE class truth, shared with both gates. The gate passes the class it derived from the plugin
        // ROUTE; with no class supplied we fall back to the name-only guess (an unrecognised plugin name is
        // presumed external), which is how the built-in "scheduled-research" once claimed to be an MCP tool.
        var resolvedClass = toolClass ?? ToolClassifier.ClassifyPresumedExternal(pendingAction.PluginName);
        var category = resolvedClass switch
        {
            ToolClass.Memory => ActionCardCategory.Memory,
            ToolClass.Todo => ActionCardCategory.Todo,
            ToolClass.Reminder => ActionCardCategory.Reminder,
            ToolClass.Files => ActionCardCategory.Files,
            ToolClass.Git => ActionCardCategory.Git,
            ToolClass.Scheduling => ActionCardCategory.Scheduled,
            // External, Unknown (a plugin name this build does not recognise, e.g. a renamed built-in) and
            // Ingest (which returns no pending action, so it never reaches a card) all render as the generic
            // external-tool card — today's shape for anything the builder cannot name.
            _ => ActionCardCategory.Mcp
        };

        // Red styling and warning copy only — not an authority rule, and no tier is withheld to signal it. The
        // git trio sheds uncommitted work yet carries no "delete", and each needs its OWN warning; the trio
        // lives in ToolPermissionService.IsWorkDiscarding so this card and the catalogue's caution agree.
        var isDelete = ToolPermissionService.IsDeleteLike(
            pendingAction.ToolName, pendingAction.ServerDeclaredDestructive);
        var isGitDestructive = ToolPermissionService.IsWorkDiscarding(pendingAction.ToolName);
        var isDestructive = isDelete || isGitDestructive;

        var warningText = pendingAction.ToolName switch
        {
            "git_switch" => _localizationService["Msg_Assistant_GitSwitchWarning"],
            "git_restore" => _localizationService["Msg_Assistant_GitRestoreWarning"],
            "git_stash" => _localizationService["Msg_Assistant_GitStashWarning"],
            _ when isDelete => pendingAction.PluginName switch
            {
                "memory" => _localizationService["Msg_Assistant_PermanentDeleteMemory"],
                "todo" => _localizationService["Msg_Assistant_PermanentDeleteTodo"],
                "reminder" => _localizationService["Msg_Assistant_PermanentDeleteReminder"],
                "files" => _localizationService["Msg_Assistant_PermanentDeleteFile"],
                // A destructive external (MCP) tool: generic warning (we can't know its exact effect).
                _ => _localizationService["Msg_Assistant_PermanentDeleteExternal"]
            },
            _ => null
        };

        // write_file and update_source carry a true line-level diff preview that bypasses the
        // Label/Value ParseKeyValueText path used for every other plugin. The card renders DiffLines
        // instead. Every other Memory-classed call (remember/forget) has no DiffPreview, so this only
        // widens the branch update_source needs without changing their rendering.
        var showsDiffPreview = category is ActionCardCategory.Files or ActionCardCategory.Memory
            && pendingAction.DiffPreview is { Count: > 0 };

        var details = new ObservableCollection<ActionCardDetail>();
        if (!showsDiffPreview && pendingAction.Details is not null)
        {
            // Memory + MCP carry structured JSON detail (MCP passes the raw tool-call arguments);
            // the built-in write plugins use key/value text. ActionCardCategory.Scheduled belongs to the
            // key/value branch — ScheduledJobToolHandler builds "Label: value" TEXT — and used to be parsed
            // as JSON here (via the Mcp mis-categorization), which silently yielded no detail rows at all.
            // Derived from the CLASS resolved above, not the plugin name: a built-in renamed through
            // ApplyServerMetadata still classifies as Memory by route, and a name test would then run its JSON
            // details through the key/value parser and render no rows.
            var parsed = resolvedClass == ToolClass.Memory || category == ActionCardCategory.Mcp
                ? JsonHelper.ParseToDetails(pendingAction.Details)
                : JsonHelper.ParseKeyValueText(pendingAction.Details);
            details = new ObservableCollection<ActionCardDetail>(DetokenizeDetails(parsed, detokenize));
        }

        // Use a `with` expression so detokenizing the text preserves OldLineNumber/NewLineNumber —
        // a positional `new DiffLine(d.Kind, …)` would silently drop them (only in PII-detokenize mode).
        var diffLines = showsDiffPreview
            ? new ObservableCollection<DiffLine>(
                pendingAction.DiffPreview!.Select(d =>
                    detokenize ? d with { Text = _tokenMapService.Detokenize(d.Text) } : d))
            : new ObservableCollection<DiffLine>();

        var title = FormatToolTitle(pendingAction.ToolName, category);

        var card = new ActionCardInfo
        {
            Title = title,
            Summary = Detokenize(pendingAction.Description, detokenize),
            Category = category,
            ToolName = pendingAction.ToolName,
            PluginId = pendingAction.PluginId,
            IsAutoApproved = autoApproved,
            IsDestructive = isDestructive,
            WarningText = warningText,
            Details = details,
            DiffLines = diffLines,
            FilePath = Detokenize(pendingAction.TargetPath ?? "", detokenize),
            AcceptedStatusText = _localizationService.Format("ActionCard_Status_Accepted", title),
            DeclinedStatusText = _localizationService.Format("ActionCard_Status_Declined", title),
            // The resolved sentence must name the tier that ran the call: "you always allow X" is false for
            // every tier but the standing grant, and sends the user hunting for a grant that was never written.
            AutoApprovedStatusText = _localizationService.Format(AutoApprovedStatusKey(autoApprovedAs), title),
            DeclineLabel = _localizationService["ActionCard_Decline"],
            AllowOnceLabel = _localizationService["ActionCard_AllowOnce"],
            AlwaysAllowLabel = _localizationService["ActionCard_AlwaysAllow"],
            AllowForSessionLabel = _localizationService["ActionCard_AllowForSession"],
        };

        // [ObservableProperty]-generated State is not init-settable; the auto-approved
        // bypass card is returned pre-resolved. A bypass card renders its diff
        // collapsed (re-expandable) — nobody asked to review it, so don't show a full-height diff.
        if (autoApproved)
        {
            card.State = ActionCardState.Accepted;
            card.IsDiffExpanded = false;
        }

        return card;
    }

    /// <summary>A helper, not an inline switch, so a localization test can enumerate the keys it returns.</summary>
    internal static string AutoApprovedStatusKey(ToolGateDecision? decision) => decision switch
    {
        ToolGateDecision.AutoApprovedStandingGrant => "ActionCard_AutoApproved",
        ToolGateDecision.AutoApprovedSessionGrant => "ActionCard_AutoApprovedForSession",
        ToolGateDecision.AutoApprovedPolicy => "ActionCard_AutoApprovedByAutonomy",
        ToolGateDecision.GrantedByName => "ActionCard_AutoApprovedByRunGrant",
        _ => "ActionCard_AutoApproved",
    };

    public string ResolveStatusText(string toolName) => toolName switch
    {
        "recall" => _localizationService["Msg_Assistant_StatusSearchingMemory"],
        "remember" => _localizationService["Msg_Assistant_StatusUpdatingMemory"],
        "forget" => _localizationService["Msg_Assistant_StatusDeletingMemory"],
        "create_reminder" => _localizationService["Msg_Assistant_StatusCreatingReminder"],
        "query_reminders" => _localizationService["Msg_Assistant_StatusCheckingReminders"],
        "update_reminder" => _localizationService["Msg_Assistant_StatusUpdatingReminder"],
        "delete_reminder" => _localizationService["Msg_Assistant_StatusDeletingReminder"],
        "create_todo" => _localizationService["Msg_Assistant_StatusCreatingTodo"],
        "query_todos" => _localizationService["Msg_Assistant_StatusCheckingTodos"],
        "complete_todo" => _localizationService["Msg_Assistant_StatusCompletingTodo"],
        "update_todo" => _localizationService["Msg_Assistant_StatusUpdatingTodo"],
        "delete_todo" => _localizationService["Msg_Assistant_StatusDeletingTodo"],
        "search_chats" => _localizationService["Msg_Assistant_StatusSearchingChats"],
        "read_chat" => _localizationService["Msg_Assistant_StatusReadingChat"],
        var t when t.StartsWith("git_", StringComparison.Ordinal) => _localizationService["Msg_Assistant_StatusRunningGit"],
        _ => _localizationService["Msg_Assistant_StatusProcessing"]
    };

    public string ResolveSuccessTitle(string pluginName) => pluginName switch
    {
        "memory" => _localizationService["Msg_Assistant_MemoryUpdated"],
        "todo" => _localizationService["Msg_Assistant_TodoUpdated"],
        "reminder" => _localizationService["Msg_Assistant_ReminderUpdated"],
        "git" => _localizationService["Msg_Assistant_GitUpdated"],
        _ => _localizationService["Msg_Assistant_StatusProcessing"]
    };

    private string FormatToolTitle(string toolName, ActionCardCategory category)
    {
        // MCP tool names are server-defined and don't map to the built-in action verbs; the card's
        // Summary already shows "{plugin}: {tool}", so the title is just the generic external-tool label.
        if (category == ActionCardCategory.Mcp)
            return _localizationService["ActionCard_Category_Mcp"];

        var categoryKey = category switch
        {
            ActionCardCategory.Memory => "ActionCard_Category_Memory",
            ActionCardCategory.Todo => "ActionCard_Category_Todo",
            ActionCardCategory.Reminder => "ActionCard_Category_Reminder",
            ActionCardCategory.Files => "ActionCard_Category_File",
            ActionCardCategory.Git => "ActionCard_Category_Git",
            ActionCardCategory.Scheduled => "ActionCard_Category_Scheduled",
            _ => "ActionCard_Category_Memory"
        };

        var actionKey = toolName switch
        {
            "create_todo" or "create_reminder" or "create_source"
                or "create_routine_from_blueprint" => "ActionCard_Action_Create",
            "remember" or "update_source" or "update_todo" or "update_reminder"
                or "update_scheduled_research" => "ActionCard_Action_Update",
            "forget" or "delete_todo" or "delete_reminder" or "delete_file"
                or "delete_scheduled_research" => "ActionCard_Action_Delete",
            "complete_todo" => "ActionCard_Action_Complete",
            "write_file" => "ActionCard_Action_Write",
            "git_init" => "ActionCard_Action_Initialize",
            "git_add" => "ActionCard_Action_Stage",
            "git_commit" => "ActionCard_Action_Commit",
            "git_switch" => "ActionCard_Action_Switch",
            "git_restore" => "ActionCard_Action_Restore",
            "git_stash" => "ActionCard_Action_Stash",
            _ => "ActionCard_Action_Create"
        };

        return $"{_localizationService[actionKey]} {_localizationService[categoryKey]}";
    }

    private string Detokenize(string text, bool detokenize) =>
        detokenize ? _tokenMapService.Detokenize(text) : text;

    private List<ActionCardDetail> DetokenizeDetails(List<ActionCardDetail> details, bool detokenize)
    {
        if (!detokenize) return details;
        return details.Select(d => new ActionCardDetail(d.Label, _tokenMapService.Detokenize(d.Value))).ToList();
    }
}
