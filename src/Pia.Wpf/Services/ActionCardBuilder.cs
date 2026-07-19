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
    private readonly IToolPermissionService _permissions;

    public ActionCardBuilder(
        ILocalizationService localizationService,
        ITokenMapService tokenMapService,
        IToolPermissionService permissions)
    {
        _localizationService = localizationService;
        _tokenMapService = tokenMapService;
        _permissions = permissions;
    }

    public ActionCardInfo Build(PluginToolCall pendingAction, bool detokenize, bool autoApproved = false)
    {
        var category = pendingAction.PluginName switch
        {
            "memory" => ActionCardCategory.Memory,
            "todo" => ActionCardCategory.Todo,
            "reminder" => ActionCardCategory.Reminder,
            "files" => ActionCardCategory.Files,
            "git" => ActionCardCategory.Git,
            // Any non-built-in plugin is an external (MCP) tool — server-defined name.
            _ => ActionCardCategory.Mcp
        };

        // Destructive is TOOLNAME-based, not the "delete" substring: git_switch/git_restore/git_stash
        // carry no "delete" yet can shed uncommitted changes, and each needs its OWN warning. (git_stash
        // "list" runs inline and never reaches a card, so marking the tool destructive here is safe.)
        var isDelete = pendingAction.ToolName.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || pendingAction.ToolName == "forget";
        var isGitDestructive = pendingAction.ToolName is "git_switch" or "git_restore" or "git_stash";
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
                _ => null
            },
            _ => null
        };

        // Files write_file carries a true line-level diff preview that bypasses the Label/Value
        // ParseKeyValueText path used for every other plugin. The card renders DiffLines instead.
        var isFilesDiff = category == ActionCardCategory.Files && pendingAction.DiffPreview is { Count: > 0 };

        var details = new ObservableCollection<ActionCardDetail>();
        if (!isFilesDiff && pendingAction.Details is not null)
        {
            // Memory + MCP carry structured JSON detail (MCP passes the raw tool-call arguments);
            // the built-in write plugins use key/value text.
            var parsed = pendingAction.PluginName == "memory" || category == ActionCardCategory.Mcp
                ? JsonHelper.ParseToDetails(pendingAction.Details)
                : JsonHelper.ParseKeyValueText(pendingAction.Details);
            details = new ObservableCollection<ActionCardDetail>(DetokenizeDetails(parsed, detokenize));
        }

        // Use a `with` expression so detokenizing the text preserves OldLineNumber/NewLineNumber —
        // a positional `new DiffLine(d.Kind, …)` would silently drop them (only in PII-detokenize mode).
        var diffLines = isFilesDiff
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
            // MCP tools are grantable as a class: they aren't in the built-in safe allowlist, but an
            // external tool is a specific named capability the user may choose to "always allow" per tool
            // (unlike the catch-all write_file, which stays never-auto-approvable). The gate re-checks this.
            IsAutoApprovable = _permissions.IsAutoApproveEligible(pendingAction.ToolName) || category == ActionCardCategory.Mcp,
            IsAutoApproved = autoApproved,
            IsDestructive = isDestructive,
            WarningText = warningText,
            Details = details,
            DiffLines = diffLines,
            FilePath = Detokenize(pendingAction.TargetPath ?? "", detokenize),
            AcceptedStatusText = _localizationService.Format("ActionCard_Status_Accepted", title),
            DeclinedStatusText = _localizationService.Format("ActionCard_Status_Declined", title),
            AutoApprovedStatusText = _localizationService.Format("ActionCard_AutoApproved", title),
            DeclineLabel = _localizationService["ActionCard_Decline"],
            AllowOnceLabel = _localizationService["ActionCard_AllowOnce"],
            AlwaysAllowLabel = _localizationService["ActionCard_AlwaysAllow"],
        };

        // [ObservableProperty]-generated State is not init-settable; the auto-approved
        // bypass card is returned pre-resolved (design §4/§7). A bypass card renders its diff
        // collapsed (re-expandable) — nobody asked to review it, so don't show a full-height diff.
        if (autoApproved)
        {
            card.State = ActionCardState.Accepted;
            card.IsDiffExpanded = false;
        }

        return card;
    }

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
            _ => "ActionCard_Category_Memory"
        };

        var actionKey = toolName switch
        {
            "create_todo" or "create_reminder" => "ActionCard_Action_Create",
            "remember" or "update_todo" or "update_reminder" => "ActionCard_Action_Update",
            "forget" or "delete_todo" or "delete_reminder" or "delete_file" => "ActionCard_Action_Delete",
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
