using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Flow producer for a server policy change. Flow rather than the snackbar because the store is a
/// cross-window singleton while the snackbar presenter is per-window markup.
/// </summary>
public sealed class PolicyNotificationSurface : IPolicyNotificationSurface
{
    private readonly IFlowService _flowService;
    private readonly ILocalizationService _localizationService;

    public PolicyNotificationSurface(IFlowService flowService, ILocalizationService localizationService)
    {
        _flowService = flowService;
        _localizationService = localizationService;
    }

    public void NotifyValuesChanged(bool restartRequired)
    {
        // No DedupKey: the key is an entity id and a policy notice has no entity, so two changes in one
        // session produce two items.
        _flowService.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Info,
            Source = FlowSource.Policy,
            Title = _localizationService["Flow_PolicyUpdated_Title"],
            Body = _localizationService[restartRequired
                ? "Flow_PolicyUpdated_Body_Restart"
                : "Flow_PolicyUpdated_Body"],
            Lifetime = FlowLifetime.Persistent,
        });
    }
}
