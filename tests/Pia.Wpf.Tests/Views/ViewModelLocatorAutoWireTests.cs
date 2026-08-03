using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pia.Navigation;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>nav:ViewModelLocator.AutoWireViewModel="True"</c> — carried by eight views — and the two things that
/// have to hold at once for it to be safe: the naming convention must resolve the RIGHT ViewModel type, and
/// resolving one must never take the <c>DataContext</c> away from whatever already supplied it.
/// <para>
/// <b>Both halves were settled by execution, in this order, on 2026-08-03 at <c>0f5be300</c>.</b> Before the
/// fix, <c>GetViewModelType</c> was
/// <c>viewName.Replace(".Views.", ".ViewModels.").Replace("View", "ViewModel")</c>: the second
/// <c>Replace</c> hits EVERY occurrence, so <c>Pia.Views.HistoryView</c> came out as
/// <c>Pia.ViewModelModels.HistoryViewModel</c> — measured null for all eight — and the attached property was
/// inert. Fixing only that string made every one of the eight views come out of construction with a
/// provider-resolved object as its <c>DataContext</c> (measured: <c>ResolvedFromProvider</c> below, in place
/// of the host's context for all 8 theory cases). That is a regression, not a fix: the eight are instantiated
/// ONLY by an <c>App.xaml</c> <c>DataTemplate</c> (verified — no other <c>&lt;views:…&gt;</c> or
/// <c>new …View()</c> site exists in <c>src/</c>), whose <c>ContentPresenter</c> supplies the window-scoped
/// instance <c>NavigationService</c> cached and already called <c>OnNavigatedTo</c> on, and which a dozen
/// controls reach through <c>{Binding DataContext.X, RelativeSource={RelativeSource AncestorType=views:…}}</c>.
/// The resolution is worse than merely redundant: the attached-property callback runs during the XAML parse,
/// before the view has a <c>Window</c>, so <c>GetScopedProvider</c> can only fall back to the ROOT provider —
/// and the ViewModels are <c>AddScoped</c> (Bootstrapper.cs:633-645), so the object it hands back is a
/// DIFFERENT instance from the window's, leaving every loaded piece of state on the orphan.
/// </para>
/// <para>
/// So the shipped shape is both: the convention is fixed, and the resolution is deferred to <c>Loaded</c> and
/// skipped when a <c>DataContext</c> is already there. <b>The deferral is not a nicety.</b> At
/// attached-property time the view has no parent, so <c>DataContext</c> reads null even for the eight — a
/// <c>null</c> check placed THERE would decide "nobody gave me one" and assign, and a local
/// <c>DataContext</c> value permanently stops inheritance. Only by <c>Loaded</c> is the host's context in
/// place, which is what makes the check mean what it says.
/// </para>
/// <para>
/// <b>What is measured and what is synthesised.</b> Two host shapes appear here, on purpose. Most facts
/// construct the view through its real <c>App.xaml</c> template and host it in a <c>Grid</c> carrying a
/// <c>DataContext</c>, parented AFTER construction — the same property-system situation a
/// <c>ContentPresenter</c> creates, in the same order, with no layout at all. ONE fact uses the real host — a
/// <c>ContentPresenter</c> with the template and a <c>Content</c> object, <c>Measure</c>d so the template is
/// applied — because the guard reads <c>DataContext</c> at <c>Loaded</c> and is sound only if the host's
/// context is already there by then, which is a claim about the production host rather than about an analogy
/// of it. <c>Measure</c> is confined to that one fact and to <c>HistoryView</c>, whose code-behind is a bare
/// <c>InitializeComponent()</c>.
/// </para>
/// <para>
/// The one step no fact here performs is the <c>Loaded</c> BROADCAST: the framework raises it only under a
/// <c>PresentationSource</c> — a <c>Window</c> on the shared, never-torn-down host, which every file in this
/// folder refuses to open. <c>Loaded</c> is raised directly on the element instead (it is a DIRECT routed
/// event, so nothing below the view sees it either), and what makes that substitution honest is the ordering:
/// the <c>ContentPresenter</c> fact measures that the context is in place at template application, and the
/// framework can only broadcast <c>Loaded</c> to an element that is already in the tree — i.e. strictly after
/// that. The opposite ordering is pinned too, in
/// <see cref="AResolutionThatWentFirst_WOULD_BlockAContextThatArrivesAfterIt"/>, because it is the one that
/// would hurt.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class ViewModelLocatorAutoWireTests
{
    /// <summary>
    /// The eight views carrying the attached property, each with the ViewModel the convention must produce —
    /// which is also the type its <c>App.xaml</c> <c>DataTemplate</c> is keyed on, so one table serves the
    /// mapping fact and the host fact. Fully qualified on purpose: <c>Pia.Views.OptimizeView</c> is the
    /// top-level one and <c>Pia.Views.SettingsViews.OptimizeView</c> is a different type with the same file
    /// name (which does NOT carry the property).
    /// <para>
    /// A view that STOPS carrying the attribute is caught by the <c>autoWired=True</c> assertion below. A NEW
    /// view that starts carrying it is not caught by anything — that would need a markup scan, and no fact in
    /// this folder claims to enumerate markup.
    /// </para>
    /// </summary>
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
        // The other half of a convention keyed on a suffix, and the fact that BOUNDS this fix: it reaches the
        // eight above and nothing else.
        //  · TodoPanelControl does not end in "View" — it must map to nothing rather than to a mangled name.
        //  · SettingsViews.GeneralView is one of the six children SettingsView hosts with
        //    DataContext="{Binding GeneralVm}". It does not carry the attached property, and its ViewModel is
        //    Pia.ViewModels.GeneralSettingsViewModel — NOT Pia.ViewModels.SettingsViews.GeneralViewModel,
        //    which is what the convention asks for. So the convention answers null for all six either way,
        //    and no re-hosted settings tab can be a consequence of fixing it.
        Assert.Null(ViewModelLocator.GetViewModelType(typeof(Pia.Views.TodoPanelControl)));
        Assert.Null(ViewModelLocator.GetViewModelType(typeof(Pia.Views.SettingsViews.GeneralView)));
    }

    [Theory]
    [MemberData(nameof(AutoWiredViews))]
    public void ConstructingAnAutoWiredView_LeavesTheHostsDataContext_InPlace(Type view, Type viewModel)
    {
        var observed = WpfStaHost.Run(() =>
        {
            // A provider that answers EVERYTHING with a non-null object, which is what makes this fact
            // non-vacuous: a provider returning null would assign null, and "no DataContext" would be
            // indistinguishable from "the locator kept its hands off".
            var probe = new RecordingProvider();
            ViewModelLocator.Initialize(probe);
            try
            {
                // Construction exactly as production does it — the real App.xaml template. Its own key is
                // pinned by DataTemplateHostedViewParseTests; a wrong type here would fail the cast.
                var template = (DataTemplate)Application.Current.Resources[new DataTemplateKey(viewModel)];
                var element = (FrameworkElement)template.LoadContent();

                var autoWired = ViewModelLocator.GetAutoWireViewModel(element);
                var atConstruction = Describe(element.DataContext, null);

                // The host, connected AFTER construction: this is the step production's ContentPresenter
                // performs, and the step a local DataContext assigned during the parse would defeat.
                var hostContext = new object();
                var host = new Grid { DataContext = hostContext };
                host.Children.Add(element);

                // The produced type is folded in so the theory's view parameter is load-bearing: a re-keyed
                // or re-typed DataTemplate would otherwise let this fact measure a DIFFERENT view and still
                // pass, which is the shape of trap this folder keeps finding.
                return $"{element.GetType().FullName} autoWired={autoWired} " +
                       $"atConstruction={atConstruction} " +
                       $"hosted={Describe(element.DataContext, hostContext)} asked=[{probe.Report}]";
            }
            finally
            {
                // Restore the process-wide static this fact had to mutate. The collection is
                // DisableParallelization and nothing outside it touches ViewModelLocator (verified), so the
                // window is one test wide; AutoWireViewModelPremiseTests reads this same static and would
                // observe a leak.
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
        // HistoryView deliberately, and it is the only view in this file whose Loaded is raised: its
        // code-behind is a bare InitializeComponent() with no Loaded handler of its own, while AssistantView's
        // OnLoaded focuses a TextBox and the top-level OptimizeView's walks to its Window. Loaded is a DIRECT
        // routed event, so nothing below the view sees this either.
        var observed = WpfStaHost.Run(() =>
        {
            var probe = new RecordingProvider();
            ViewModelLocator.Initialize(probe);
            try
            {
                // (1) Nothing hosts it, so the deferred resolution is the only source of a DataContext —
                // which is the case a future view without an App.xaml template would be in, and the only way
                // to observe that the convention is live through the real attached-property path.
                var lone = LoadHistoryView();
                RaiseLoaded(lone);
                var loneLine = $"lone={Describe(lone.DataContext, null)} asked=[{probe.Report}]";

                // (2) The eight views' case: a host supplied one first. Same event, opposite decision.
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
        // The reachability question the guard rests on, executed rather than reasoned about: the guard reads
        // DataContext at Loaded, so it is only sound if the host's context is ALREADY there by then. Here the
        // host is the real thing rather than an analogy — a ContentPresenter with the App.xaml template and a
        // Content object, which is what MainWindow's NavigationContentPresenter becomes once
        // MainWindowViewModel.CurrentView is set. Measure() applies the template (creating the view, firing
        // the attached property) and parents it; it raises NO Loaded, because that needs a PresentationSource,
        // i.e. a Window on the shared host — which is the one thing every file in this folder refuses to open.
        // So the Loaded broadcast is the single step still synthesised, and the framework only ever broadcasts
        // it to elements already IN the tree measured here.
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
        // The ordering the deferral exists to keep out of reach, pinned rather than assumed away — because
        // "the guard reads a state that is transiently wrong" is the exact shape that has bitten this branch
        // four times. A locally assigned DataContext beats an inherited one FOREVER: if a Loaded ever reached
        // one of the eight before its host had supplied a context, the view would keep the provider's object
        // and the host's would be ignored from then on. The fact above is what puts that out of reach on the
        // only path that instantiates them; this one says what the cost would be if a future host reordered it,
        // so nobody has to rediscover it.
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

    /// <summary>
    /// Raises <see cref="FrameworkElement.LoadedEvent"/> on <paramref name="element"/> itself. The real
    /// framework raises it once the element is under a <c>PresentationSource</c>, which would mean opening a
    /// window on the shared host; the handler the locator attached is invoked identically either way, and
    /// nothing here depends on <c>IsLoaded</c>.
    /// </summary>
    private static void RaiseLoaded(FrameworkElement element) =>
        element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, element));

    /// <summary>
    /// One readable token per possible outcome, so a failure names WHICH of them happened instead of printing
    /// two object hashes.
    /// </summary>
    private static string Describe(object? dataContext, object? hostContext) => dataContext switch
    {
        null => "<null>",
        ResolvedFromProvider resolved => $"<resolved {resolved.ServiceType.Name}>",
        _ when ReferenceEquals(dataContext, hostContext) => "<host's own DataContext>",
        _ => $"<{dataContext.GetType().Name}>",
    };

    /// <summary>
    /// Stands in for the root <c>IServiceProvider</c> <c>Bootstrapper</c> installs, and records what the
    /// locator asked it for — the only way to see the convention's result from outside.
    /// </summary>
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

    /// <summary>What the probe hands back: a stand-in for the ViewModel instance a real provider would
    /// resolve. Not a ViewModel, because <c>DataContext</c> is an <c>object</c> and constructing eight real
    /// ViewModels would need the whole container.</summary>
    private sealed record ResolvedFromProvider(Type ServiceType);
}
