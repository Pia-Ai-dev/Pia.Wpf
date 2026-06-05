using System.Collections;
using System.Windows.Data;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// WPF-backed <see cref="ICollectionViewService"/>. Resolves the default
/// <see cref="System.ComponentModel.ICollectionView"/> for a bound collection
/// and sets its filter, keeping System.Windows.Data out of ViewModels.
/// </summary>
public sealed class CollectionViewService : ICollectionViewService
{
    public void ApplyFilter(IEnumerable source, Predicate<object>? filter)
    {
        var view = CollectionViewSource.GetDefaultView(source);
        if (view is null)
            return;

        view.Filter = filter;
    }
}
