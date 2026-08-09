using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pia.Navigation;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

// A locally assigned DataContext permanently stops inheritance, which is why the locator defers its resolution
// to Loaded and skips a view that already has one.
[Collection("WpfApplicationStatic")]
public class ViewModelLocatorAutoWireTests
{
    // OptimizeView is fully qualified because SettingsViews holds a different type of the same name.
    // A view that STARTS carrying the attribute is caught by nothing here — that would need a markup scan.
    public static TheoryData<Type, Type> AutoWiredViews => new()
    {
        { typeof(Pia.Views.AssistantHistoryView), typeof(AssistantHistoryViewModel) },
        { typeof(Pia.Views.AssistantView), typeof(AssistantViewModel) },
        { typeof(Pia.Views.HistoryView), typeof(HistoryViewModel) },
        { typeof(Pia.Views.MemoryView), typeof(MemoryViewModel) },
        { typeof(Pia.Views.OptimizeView), typeof(OptimizeViewModel) },
        { typeof(Pia.Views.RemindersView), typeof(RemindersViewModel) },
        { typeof(Pia.Views.SettingsView), typeof(SettingsViewModel) },
        { typeof(Pia.Views.TodoView), typeof(TodoViewModel) },
    };

    [Theory]
    [MemberData(nameof(AutoWiredViews))]
    public void TheNamingConvention_ResolvesEachAutoWiredView_ToItsRealViewModelType(Type view, Type viewModel) =>
        Assert.Equal(viewModel, ViewModelLocator.GetViewModelType(view));

    [Fact]
    public void TheNamingConvention_ResolvesNothing_ForTheViewSideTypesThatDoNotUseIt()
    {
        // TodoPanelControl does not end in "View"; the settings children are named GeneralSettingsViewModel
        // rather than SettingsViews.GeneralViewModel, so the convention answers null for those too.
        Assert.Null(ViewModelLocator.GetViewModelType(typeof(Pia.Views.TodoPanelControl)));
        Assert.Null(ViewModelLocator.GetViewModelType(typeof(Pia.Views.SettingsViews.GeneralView)));
    }

    [Theory]
    [MemberData(nameof(AutoWiredViews))]
    public void ConstructingAnAutoWiredView_LeavesTheHostsDataContext_InPlace(Type view, Type viewModel)
    {
        var observed = WpfStaHost.Run(() =>
        {
            // The probe answers everything with a non-null object, so "no DataContext" cannot pass for "hands off".
            var probe = new RecordingProvider();
            ViewModelLocator.Initialize(probe);
            try
            {
                var template = (DataTemplate)Application.Current.Resources[new DataTemplateKey(viewModel)];
                var element = (FrameworkElement)template.LoadContent();

                var autoWired = ViewModelLocator.GetAutoWireViewModel(element);
                var atConstruction = Describe(element.DataContext, null);

                // Hosted only AFTER construction, the order production's ContentPresenter uses.
                var hostContext = new object();
                var host = new Grid { DataContext = hostContext };
                host.Children.Add(element);

                return $"{element.GetType().FullName} autoWired={autoWired} " +
                       $"atConstruction={atConstruction} " +
                       $"hosted={Describe(element.DataContext, hostContext)} asked=[{probe.Report}]";
            }
            finally
            {
                // Restore the process-wide static; the collection is DisableParallelization, so the window is
                // one test wide.
                ViewModelLocator.Initialize(null!);
            }
        });

        Assert.Equal(
            $"{view.FullName} autoWired=True atConstruction=<null> " +
            "hosted=<host's own DataContext> asked=[]", observed);
    }

    [Fact]
    public void TheDeferredResolution_AssignsOnlyWhenNothingElseSuppliedADataContext()
    {
        // HistoryView because its code-behind is a bare InitializeComponent() with no Loaded handler of its own.
        var observed = WpfStaHost.Run(() =>
        {
            var probe = new RecordingProvider();
            ViewModelLocator.Initialize(probe);
            try
            {
                // (1) Nothing hosts it, so the deferred resolution is the only possible source of a DataContext.
                var lone = LoadHistoryView();
                RaiseLoaded(lone);
                var loneLine = $"lone={Describe(lone.DataContext, null)} asked=[{probe.Report}]";

                // (2) A host supplied one first: same event, opposite decision.
                probe.Reset();
                var hosted = LoadHistoryView();
                var hostContext = new object();
                var host = new Grid { DataContext = hostContext };
                host.Children.Add(hosted);
                RaiseLoaded(hosted);

                return $"{loneLine} | hosted={Describe(hosted.DataContext, hostContext)} asked=[{probe.Report}]";
            }
            finally
            {
                ViewModelLocator.Initialize(null!);
            }
        });

        Assert.Equal(
            "lone=<resolved HistoryViewModel> asked=[HistoryViewModel] | " +
            "hosted=<host's own DataContext> asked=[]", observed);
    }

    [Fact]
    public void TheProductionHost_HasAlreadySuppliedTheDataContext_BeforeLoadedIsReached()
    {
        // Measure() applies the template and parents the view, but raises no Loaded — that needs a
        // PresentationSource, i.e. a Window no test in this folder opens.
        var observed = WpfStaHost.Run(() =>
        {
            var probe = new RecordingProvider();
            ViewModelLocator.Initialize(probe);
            try
            {
                var hostContext = new object();
                var presenter = new ContentPresenter
                {
                    ContentTemplate = (DataTemplate)Application.Current.Resources[
                        new DataTemplateKey(typeof(HistoryViewModel))],
                    Content = hostContext,
                };
                presenter.Measure(new Size(1000, 1000));

                var child = VisualTreeHelper.GetChildrenCount(presenter) == 1
                    ? VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement
                    : null;
                if (child is null)
                    return "<the presenter produced no single FrameworkElement child>";

                var atTemplateApplication = Describe(child.DataContext, hostContext);
                RaiseLoaded(child);

                return $"child={child.GetType().FullName} " +
                       $"autoWired={ViewModelLocator.GetAutoWireViewModel(child)} " +
                       $"atTemplateApplication={atTemplateApplication} " +
                       $"afterLoaded={Describe(child.DataContext, hostContext)} asked=[{probe.Report}]";
            }
            finally
            {
                ViewModelLocator.Initialize(null!);
            }
        });

        Assert.Equal(
            "child=Pia.Views.HistoryView autoWired=True " +
            "atTemplateApplication=<host's own DataContext> afterLoaded=<host's own DataContext> asked=[]",
            observed);
    }

    [Fact]
    public void AResolutionThatWentFirst_WOULD_BlockAContextThatArrivesAfterIt()
    {
        // A locally assigned DataContext beats an inherited one forever: a resolution that ran before the host
        // supplied a context would keep the provider's object and ignore the host's from then on.
        var observed = WpfStaHost.Run(() =>
        {
            var probe = new RecordingProvider();
            ViewModelLocator.Initialize(probe);
            try
            {
                var view = LoadHistoryView();
                RaiseLoaded(view);
                var beforeHosting = Describe(view.DataContext, null);

                var hostContext = new object();
                var host = new Grid { DataContext = hostContext };
                host.Children.Add(view);

                return $"beforeHosting={beforeHosting} afterHosting={Describe(view.DataContext, hostContext)}";
            }
            finally
            {
                ViewModelLocator.Initialize(null!);
            }
        });

        Assert.Equal(
            "beforeHosting=<resolved HistoryViewModel> afterHosting=<resolved HistoryViewModel>", observed);
    }

    private static FrameworkElement LoadHistoryView() =>
        (FrameworkElement)((DataTemplate)Application.Current.Resources[
            new DataTemplateKey(typeof(HistoryViewModel))]).LoadContent();

    /// <summary>Raised directly on the element: the framework would need a PresentationSource, i.e. a window.</summary>
    private static void RaiseLoaded(FrameworkElement element) =>
        element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, element));

    private static string Describe(object? dataContext, object? hostContext) => dataContext switch
    {
        null => "<null>",
        ResolvedFromProvider resolved => $"<resolved {resolved.ServiceType.Name}>",
        _ when ReferenceEquals(dataContext, hostContext) => "<host's own DataContext>",
        _ => $"<{dataContext.GetType().Name}>",
    };

    private sealed class RecordingProvider : IServiceProvider
    {
        private readonly List<Type> _requested = [];

        public string Report => string.Join(", ", _requested.Select(t => t.Name));

        public void Reset() => _requested.Clear();

        public object GetService(Type serviceType)
        {
            _requested.Add(serviceType);
            return new ResolvedFromProvider(serviceType);
        }
    }

    // Not a real ViewModel: constructing eight of those would need the whole container.
    private sealed record ResolvedFromProvider(Type ServiceType);
}
