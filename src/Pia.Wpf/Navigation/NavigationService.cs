using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Pia.Navigation;

public partial class NavigationService : ObservableObject, INavigationService, IDisposable
{
    private bool _disposed;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Type, ObservableObject> _viewModelCache = new();
    private readonly Stack<Type> _navigationStack = new();

    [ObservableProperty]
    private ObservableObject? _currentViewModel;
    public event Action<ObservableObject>? ViewModelChanged;
    public bool CanGoBack => _navigationStack.Count > 1;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void NavigateTo<TViewModel>() where TViewModel : ObservableObject
    {
        _ = NavigateToInternalAsync(typeof(TViewModel), null);
    }

    public void NavigateTo<TViewModel, TParameter>(TParameter parameter) where TViewModel : ObservableObject
    {
        _ = NavigateToInternalAsync(typeof(TViewModel), parameter);
    }

    public Task NavigateToAsync<TViewModel>() where TViewModel : ObservableObject
    {
        return NavigateToInternalAsync(typeof(TViewModel), null);
    }

    public Task NavigateToAsync<TViewModel, TParameter>(TParameter parameter) where TViewModel : ObservableObject
    {
        return NavigateToInternalAsync(typeof(TViewModel), parameter);
    }

    public void GoBack()
    {
        _ = GoBackInternalAsync();
    }

    public Task GoBackAsync()
    {
        return GoBackInternalAsync();
    }

    private async Task GoBackInternalAsync()
    {
        if (!CanGoBack)
            return;

        _navigationStack.Pop(); // Remove current
        var previousType = _navigationStack.Peek();
        await NavigateToInternalAsync(previousType, null, addToStack: false);
    }

    private async Task NavigateToInternalAsync(Type viewModelType, object? parameter, bool addToStack = true)
    {
        // Notify current ViewModel of navigation away (sync first, then async)
        if (CurrentViewModel is INavigationAware currentNavigationAware)
        {
            currentNavigationAware.OnNavigatedFrom();
            await currentNavigationAware.OnNavigatedFromAsync();
        }

        // Get or create ViewModel
        if (!_viewModelCache.TryGetValue(viewModelType, out var viewModel))
        {
            viewModel = (ObservableObject)_serviceProvider.GetRequiredService(viewModelType);
            _viewModelCache[viewModelType] = viewModel;
        }

        CurrentViewModel = viewModel;

        if (addToStack)
        {
            _navigationStack.Push(viewModelType);
        }

        ViewModelChanged?.Invoke(viewModel);

        // Notify new ViewModel of navigation to (sync first for immediate setup, then async for data loading)
        if (viewModel is INavigationAware navigationAware)
        {
            navigationAware.OnNavigatedTo(parameter);
            await navigationAware.OnNavigatedToAsync(parameter);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Dispose all cached ViewModels
        foreach (var viewModel in _viewModelCache.Values)
        {
            if (viewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _viewModelCache.Clear();
        _navigationStack.Clear();
    }
}
