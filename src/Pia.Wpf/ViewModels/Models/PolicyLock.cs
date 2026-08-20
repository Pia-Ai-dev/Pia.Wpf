using System.ComponentModel;
using System.Threading;
using System.Windows.Data;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>XAML-facing view of enterprise policy: <c>IsEnabled="{Binding Policy[Theme]}"</c> is false
/// while that setting is enforced. Raises for the indexer when the enforced set moves.</summary>
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
        _policyService.LocksChanged += OnLocksChanged;
    }

    public bool this[string settingName] => !_policyService.IsEnforced(settingName);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _policyService.LocksChanged -= OnLocksChanged;
    }

    // LocksChanged arrives on the pull thread; a bound indexer must be invalidated on the UI thread.
    private void OnLocksChanged(object? sender, EventArgs e)
    {
        if (_sync is null)
            PropertyChanged?.Invoke(this, IndexerChanged);
        else
            _sync.Post(_ => PropertyChanged?.Invoke(this, IndexerChanged), null);
    }
}
