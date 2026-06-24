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
            _ => ActionCardCategory.Memory
        };

        var isDelete = pendingAction.ToolName.Contains("delete");

        var warningText = isDelete ? pendingAction.PluginName switch
        {
            "memory" => _localizationService["Msg_Assistant_PermanentDeleteMemory"],
            "todo" => _localizationService["Msg_Assistant_PermanentDeleteTodo"],
            "reminder" => _localizationService["Msg_Assistant_PermanentDeleteReminder"],
            "files" => _localizationService["Msg_Assistant_PermanentDeleteFile"],
            _ => null
        } : null;

        // Files write_file carries a true line-level diff preview that bypasses the Label/Value
        // ParseKeyValueText path used for every other plugin. The card renders DiffLines instead.
        var isFilesDiff = category == ActionCardCategory.Files && pendingAction.DiffPreview is { Count: > 0 };

        var details = new ObservableCollection<ActionCardDetail>();
        if (!isFilesDiff && pendingAction.Details is not null)
        {
            // Memory carries structured JSON detail; every other plugin uses key/value text.
            var parsed = pendingAction.PluginName == "memory"
                ? JsonHelper.ParseToDetails(pendingAction.Details)
                : JsonHelper.ParseKeyValueText(pendingAction.Details);
            details = new ObservableCollection<ActionCardDetail>(DetokenizeDetails(parsed, detokenize));
        }

        var diffLines = isFilesDiff
            ? new ObservableCollection<DiffLine>(
                pendingAction.DiffPreview!.Select(d =>
                    new DiffLine(d.Kind, detokenize ? _tokenMapService.Detokenize(d.Text) : d.Text)))
            : new ObservableCollection<DiffLine>();

        var title = FormatToolTitle(pendingAction.ToolName, category);

        var card = new ActionCardInfo
        {
            Title = title,
            Summary = Detokenize(pendingAction.Description, detokenize),
            Category = category,
            ToolName = pendingAction.ToolName,
            PluginId = pendingAction.PluginId,
            IsAutoApprovable = _permissions.IsAutoApproveEligible(pendingAction.ToolName),
            IsAutoApproved = autoApproved,
            IsDestructive = isDelete,
            WarningText = warningText,
            Details = details,
            DiffLines = diffLines,
            AcceptedStatusText = _localizationService.Format("ActionCard_Status_Accepted", title),
            DeclinedStatusText = _localizationService.Format("ActionCard_Status_Declined", title),
            AutoApprovedStatusText = _localizationService.Format("ActionCard_AutoApproved", title),
            DeclineLabel = _localizationService["ActionCard_Decline"],
            AllowOnceLabel = _localizationService["ActionCard_AllowOnce"],
            AlwaysAllowLabel = _localizationService["ActionCard_AlwaysAllow"],
        };

        // [ObservableProperty]-generated State is not init-settable; the auto-approved
        // bypass card is returned pre-resolved (design §4/§7).
        if (autoApproved)
        {
            card.State = ActionCardState.Accepted;
        }

        return card;
    }

    public string ResolveStatusText(string toolName) => toolName switch
    {
        "list_memories" => _localizationService["Msg_Assistant_StatusCheckingMemory"],
        "query_memory" => _localizationService["Msg_Assistant_StatusSearchingMemory"],
        "create_object" => _localizationService["Msg_Assistant_StatusCreatingMemory"],
        "update_object" => _localizationService["Msg_Assistant_StatusUpdatingMemory"],
        "append_to_list" => _localizationService["Msg_Assistant_StatusUpdatingMemory"],
        "delete_object" => _localizationService["Msg_Assistant_StatusDeletingMemory"],
        "create_reminder" => _localizationService["Msg_Assistant_StatusCreatingReminder"],
        "query_reminders" => _localizationService["Msg_Assistant_StatusCheckingReminders"],
        "update_reminder" => _localizationService["Msg_Assistant_StatusUpdatingReminder"],
        "delete_reminder" => _localizationService["Msg_Assistant_StatusDeletingReminder"],
        "create_todo" => _localizationService["Msg_Assistant_StatusCreatingTodo"],
        "query_todos" => _localizationService["Msg_Assistant_StatusCheckingTodos"],
        "complete_todo" => _localizationService["Msg_Assistant_StatusCompletingTodo"],
        "update_todo" => _localizationService["Msg_Assistant_StatusUpdatingTodo"],
        "delete_todo" => _localizationService["Msg_Assistant_StatusDeletingTodo"],
        _ => _localizationService["Msg_Assistant_StatusProcessing"]
    };

    public string ResolveSuccessTitle(string pluginName) => pluginName switch
    {
        "memory" => _localizationService["Msg_Assistant_MemoryUpdated"],
        "todo" => _localizationService["Msg_Assistant_TodoUpdated"],
        "reminder" => _localizationService["Msg_Assistant_ReminderUpdated"],
        _ => _localizationService["Msg_Assistant_StatusProcessing"]
    };

    private string FormatToolTitle(string toolName, ActionCardCategory category)
    {
        var categoryKey = category switch
        {
            ActionCardCategory.Memory => "ActionCard_Category_Memory",
            ActionCardCategory.Todo => "ActionCard_Category_Todo",
            ActionCardCategory.Reminder => "ActionCard_Category_Reminder",
            ActionCardCategory.Files => "ActionCard_Category_File",
            _ => "ActionCard_Category_Memory"
        };

        var actionKey = toolName switch
        {
            "create_object" or "create_todo" or "create_reminder" => "ActionCard_Action_Create",
            "update_object" or "append_to_list" or "update_todo" or "update_reminder" => "ActionCard_Action_Update",
            "delete_object" or "delete_todo" or "delete_reminder" or "delete_file" => "ActionCard_Action_Delete",
            "complete_todo" => "ActionCard_Action_Complete",
            "write_file" => "ActionCard_Action_Write",
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
