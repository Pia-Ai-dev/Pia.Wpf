using System.ComponentModel;
using System.Threading;
using System.Windows.Data;
using Pia.Localization;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>XAML-facing view of enterprise policy: <c>IsEnabled="{Binding Policy[Theme]}"</c> is false
/// while that setting is enforced, and <c>ToolTip="{Binding Policy.Reason[Theme]}"</c> says why. Raises for
/// the indexer when the enforced set moves.</summary>
public sealed class PolicyLock : INotifyPropertyChanged, IDisposable
{
    private static readonly PropertyChangedEventArgs IndexerChanged = new(Binding.IndexerName);

    private readonly IPolicyService _policyService;
    private readonly SynchronizationContext? _sync;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PolicyLock(IPolicyService policyService)
    {
        _policyService = policyService;
        _sync = SynchronizationContext.Current;
        Reason = new PolicyReason(policyService);
        _policyService.LocksChanged += OnLocksChanged;
        LocalizationSource.Instance.PropertyChanged += OnLanguageChanged;
    }

    public bool this[string settingName] => !_policyService.IsEnforced(settingName);

    /// <summary>Null unless the setting is enforced, and a null <c>ToolTip</c> shows nothing — so one
    /// binding covers both states without a trigger per control.</summary>
    public PolicyReason Reason { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _policyService.LocksChanged -= OnLocksChanged;
        LocalizationSource.Instance.PropertyChanged -= OnLanguageChanged;
    }

    // LocksChanged arrives on the pull thread; a bound indexer must be invalidated on the UI thread.
    private void OnLocksChanged(object? sender, EventArgs e)
    {
        if (_sync is null)
            RaiseIndexerChanged();
        else
            _sync.Post(_ => RaiseIndexerChanged(), null);
    }

    // The reason text is read through the XAML localization singleton, which does not re-evaluate a
    // binding that is not rooted in it.
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => Reason.Invalidate();

    private void RaiseIndexerChanged()
    {
        PropertyChanged?.Invoke(this, IndexerChanged);
        Reason.Invalidate();
    }
}

/// <summary>The <see cref="PolicyLock.Reason"/> companion: why a control is locked, or null when it is not.</summary>
public sealed class PolicyReason : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IndexerChanged = new(Binding.IndexerName);

    private readonly IPolicyService _policyService;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal PolicyReason(IPolicyService policyService) => _policyService = policyService;

    public string? this[string settingName] =>
        _policyService.IsEnforced(settingName) ? LocalizationSource.Instance["Policy_ManagedTooltip"] : null;

    internal void Invalidate() => PropertyChanged?.Invoke(this, IndexerChanged);
}
