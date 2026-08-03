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

        // ALWAYS defer to Loaded, never resolve here — the deferral is what makes the guard in
        // OnElementLoaded mean anything. This callback runs during the XAML parse, when the element has no
        // parent yet, so DataContext reads null even for a view an App.xaml DataTemplate is about to host: a
        // check placed HERE would conclude "nobody gave me one", assign, and a LOCAL DataContext value
        // permanently stops the inheritance that was on its way. Measured on 2026-08-03 — resolving here put
        // a root-scope object on all eight views that carry this property (see ViewModelLocatorAutoWireTests).
        // Deferring costs nothing: the element cannot be visible before Loaded either way.
        element.Loaded += OnElementLoaded;
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Loaded -= OnElementLoaded;

        // Never overwrite a DataContext something else supplied. All eight views carrying this property are
        // instantiated ONLY by an App.xaml DataTemplate, whose ContentPresenter hands them the window-scoped
        // ViewModel that NavigationService cached and already called OnNavigatedTo on — and that is the
        // instance a dozen controls reach through {Binding DataContext.X, RelativeSource={RelativeSource
        // AncestorType=views:…}}. Auto-wiring is therefore the FALLBACK for a view nothing else roots, not a
        // second opinion about a view that is already rooted.
        if (element.DataContext is not null)
            return;

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

    /// <summary>
    /// The view → ViewModel naming convention, as a pure function. <c>internal</c> rather than
    /// <c>private</c> because <c>Pia.Wpf.Tests</c> pins the mapping for every view that carries
    /// <see cref="AutoWireViewModelProperty"/>, and the mapping is the half that was silently wrong.
    /// </summary>
    internal static Type? GetViewModelType(Type viewType)
    {
        var viewName = viewType.FullName;
        if (viewName is null)
            return null;

        // Convention: Pia.Views.OptimizeView -> Pia.ViewModels.OptimizeViewModel. Only the TRAILING "View"
        // becomes "ViewModel", by appending: a `.Replace("View", "ViewModel")` also rewrites the namespace
        // segment the line above just produced (".ViewModels." -> ".ViewModelModels."), which is what made
        // this convention resolve nothing at all for every one of the eight views that rely on it.
        if (!viewName.EndsWith("View", StringComparison.Ordinal))
            return null;

        var viewModelName = viewName.Replace(".Views.", ".ViewModels.") + "Model";

        return viewType.Assembly.GetType(viewModelName);
    }

    public static T GetService<T>() where T : class
    {
        if (_serviceProvider is null)
            throw new InvalidOperationException("ViewModelLocator not initialized");

        return _serviceProvider.GetRequiredService<T>();
    }
}
