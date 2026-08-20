using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// XAML-facing view of enterprise policy: <c>IsEnabled="{Binding Policy[Theme]}"</c> is false while
/// that setting is enforced. Returns editability rather than enforcement so no converter is needed.
/// Enforcement can change mid-session, so a re-read has to be driven from outside this indexer.
/// </summary>
public sealed class PolicyLock
{
    private readonly IPolicyService _policyService;

    public PolicyLock(IPolicyService policyService) => _policyService = policyService;

    public bool this[string settingName] => !_policyService.IsEnforced(settingName);
}
