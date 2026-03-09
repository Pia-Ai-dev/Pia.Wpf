using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Pia.Navigation;

public static class ViewModelLocator
{
    private static IServiceProvider? _serviceProvider;

    public static void Initialize(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static readonly DependencyProperty AutoWireViewModelProperty =
        DependencyProperty.RegisterAttached(
            "AutoWireViewModel",
            typeof(bool),
            typeof(ViewModelLocator),
            new PropertyMetadata(false, OnAutoWireViewModelChanged));

    public static bool GetAutoWireViewModel(DependencyObject obj) =>
        (bool)obj.GetValue(AutoWireViewModelProperty);

    public static void SetAutoWireViewModel(DependencyObject obj, bool value) =>
        obj.SetValue(AutoWireViewModelProperty, value);

    public static readonly DependencyProperty ScopedServiceProviderProperty =
        DependencyProperty.RegisterAttached(
            "ScopedServiceProvider",
            typeof(IServiceProvider),
            typeof(ViewModelLocator),
            new PropertyMetadata(null));

    public static IServiceProvider? GetScopedServiceProvider(DependencyObject obj) =>
        (IServiceProvider?)obj.GetValue(ScopedServiceProviderProperty);

    public static void SetScopedServiceProvider(DependencyObject obj, IServiceProvider value) =>
        obj.SetValue(ScopedServiceProviderProperty, value);

    private static void OnAutoWireViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
            return;

        ResolveViewModel(element);
    }

    private static void ResolveViewModel(FrameworkElement element)
    {
        var provider = GetScopedProvider(element);

        if (provider is null)
        {
            // View not yet in visual tree — defer until Loaded
            element.Loaded += OnElementLoaded;
            return;
        }

        SetViewModelFromProvider(element, provider);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Loaded -= OnElementLoaded;

        var provider = GetScopedProvider(element);
        if (provider is not null)
        {
            SetViewModelFromProvider(element, provider);
        }
    }

    private static void SetViewModelFromProvider(FrameworkElement element, IServiceProvider provider)
    {
        var viewType = element.GetType();
        var viewModelType = GetViewModelType(viewType);

        if (viewModelType is not null)
        {
            var viewModel = provider.GetService(viewModelType);
            element.DataContext = viewModel;
        }
    }

    private static IServiceProvider? GetScopedProvider(FrameworkElement element)
    {
        var window = Window.GetWindow(element);
        if (window is not null)
        {
            var scoped = GetScopedServiceProvider(window);
            if (scoped is not null)
                return scoped;
        }

        // Fallback to root provider (design-time or early resolution)
        return _serviceProvider;
    }

    private static Type? GetViewModelType(Type viewType)
    {
        var viewName = viewType.FullName;
        if (viewName is null)
            return null;

        // Convention: Pia.Views.OptimizeView -> Pia.ViewModels.OptimizeViewModel
        var viewModelName = viewName
            .Replace(".Views.", ".ViewModels.")
            .Replace("View", "ViewModel");

        return viewType.Assembly.GetType(viewModelName);
    }

    public static T GetService<T>() where T : class
    {
        if (_serviceProvider is null)
            throw new InvalidOperationException("ViewModelLocator not initialized");

        return _serviceProvider.GetRequiredService<T>();
    }
}
