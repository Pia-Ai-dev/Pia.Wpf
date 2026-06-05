using System.Collections;

namespace Pia.Services.Interfaces;

/// <summary>
/// Abstraction over WPF's default collection view so ViewModels can apply
/// live filtering without referencing System.Windows.Data directly.
/// Implemented in the WPF layer.
/// </summary>
public interface ICollectionViewService
{
    /// <summary>
    /// Applies <paramref name="filter"/> to the default view of <paramref name="source"/>,
    /// or clears any existing filter when <paramref name="filter"/> is null.
    /// </summary>
    void ApplyFilter(IEnumerable source, Predicate<object>? filter);
}
