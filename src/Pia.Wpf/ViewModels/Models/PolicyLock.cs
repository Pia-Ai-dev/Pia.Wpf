using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// XAML-facing view of enterprise policy: <c>IsEnabled="{Binding Policy[Theme]}"</c> is false while
/// that setting is enforced. Returns editability rather than enforcement so no converter is needed.
/// Policy is loaded once per process, so this deliberately raises no change notification.
/// </summary>
public sealed class PolicyLock
{
    private readonly IPolicyService _policyService;

    public PolicyLock(IPolicyService policyService) => _policyService = policyService;

    public bool this[string settingName] => !_policyService.IsEnforced(settingName);
}
