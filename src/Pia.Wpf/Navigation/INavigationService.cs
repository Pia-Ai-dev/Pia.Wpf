using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Navigation;

public interface INavigationService
{
    ObservableObject? CurrentViewModel { get; }
    event Action<ObservableObject>? ViewModelChanged;

    void NavigateTo<TViewModel>() where TViewModel : ObservableObject;
    void NavigateTo<TViewModel, TParameter>(TParameter parameter) where TViewModel : ObservableObject;

    Task NavigateToAsync<TViewModel>() where TViewModel : ObservableObject;
    Task NavigateToAsync<TViewModel, TParameter>(TParameter parameter) where TViewModel : ObservableObject;

    bool CanGoBack { get; }
    void GoBack();
    Task GoBackAsync();
}

public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);
    void OnNavigatedFrom();

    /// <summary>
    /// Called asynchronously after navigation completes and the view is ready.
    /// Use this for async initialization instead of calling async methods in constructors.
    /// </summary>
    Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;

    /// <summary>
    /// Called asynchronously when navigating away from this ViewModel.
    /// Use this for async cleanup operations.
    /// </summary>
    Task OnNavigatedFromAsync() => Task.CompletedTask;
}
