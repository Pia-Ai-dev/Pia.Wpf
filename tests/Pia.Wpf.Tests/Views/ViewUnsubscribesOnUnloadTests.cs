using System.ComponentModel;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A view that resolves its VM through <c>DataContext as T</c> inside <c>OnUnloaded</c> silently skips the
/// unsubscribe when the host cleared DataContext first, and the long-lived VM then pins the whole visual tree.
/// A 2026-09-03 production dump held 18 AssistantView instances that way.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ViewUnsubscribesOnUnloadTests
{
    [Fact]
    public void AssistantView_detaches_from_the_view_model_when_DataContext_is_cleared_before_unload()
    {
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        int stillSubscribed;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                view = new AssistantView { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            stillSubscribed = RunLoadUnloadCycle<AssistantView>(() => view!, () => vm!);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.Equal(0, stillSubscribed);
    }

    [Fact]
    public void OptimizeView_detaches_from_the_view_model_when_DataContext_is_cleared_before_unload()
    {
        OptimizeViewModel? vm = null;
        OptimizeView? view = null;
        int stillSubscribed;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateOptimizeViewModel();
                view = new OptimizeView { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            stillSubscribed = RunLoadUnloadCycle<OptimizeView>(() => view!, () => vm!);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.Equal(0, stillSubscribed);
    }

    [Fact]
    public void AssistantView_leaves_no_duplicate_subscription_when_Loaded_fires_twice()
    {
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        int stillSubscribed;

        try
        {
            WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                view = new AssistantView { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            stillSubscribed = RunDoubleLoadCycle<AssistantView>(() => view!, () => vm!);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.Equal(0, stillSubscribed);
    }

    // Loaded, then DataContext cleared, then Unloaded — the order a ContentControl produces when it swaps views.
    private static int RunLoadUnloadCycle<TView>(Func<FrameworkElement> view, Func<INotifyPropertyChanged> vm)
        where TView : FrameworkElement
    {
        WpfStaHost.Run(() =>
        {
            var v = view();
            v.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, v));
            return 0;
        });
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            var v = view();
            v.DataContext = null;
            v.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, v));
            return 0;
        });
        WpfStaHost.Pump();

        return WpfStaHost.Run(() => CountSubscribers<TView>(vm()));
    }

    // WPF re-raises Loaded on re-parenting and template reapplication without an Unloaded in between.
    private static int RunDoubleLoadCycle<TView>(Func<FrameworkElement> view, Func<INotifyPropertyChanged> vm)
        where TView : FrameworkElement
    {
        foreach (var _ in Enumerable.Range(0, 2))
        {
            WpfStaHost.Run(() =>
            {
                var v = view();
                v.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, v));
                return 0;
            });
            WpfStaHost.Pump();
        }

        // DataContext deliberately left intact, so only the duplicate subscription can fail this.
        WpfStaHost.Run(() =>
        {
            var v = view();
            v.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, v));
            return 0;
        });
        WpfStaHost.Pump();

        return WpfStaHost.Run(() => CountSubscribers<TView>(vm()));
    }

    private static int CountSubscribers<TTarget>(INotifyPropertyChanged source)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(nameof(INotifyPropertyChanged.PropertyChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(source) is MulticastDelegate handler)
                return handler.GetInvocationList().Count(d => d.Target is TTarget);
        }

        return 0;
    }

    private static OptimizeViewModel CreateOptimizeViewModel() => new(
        NullLogger<OptimizeViewModel>.Instance,
        Substitute.For<ITextOptimizationService>(),
        Substitute.For<ITemplateService>(),
        Substitute.For<ISettingsService>(),
        Substitute.For<IOutputService>(),
        Substitute.For<IHistoryService>(),
        Substitute.For<IProviderService>(),
        Substitute.For<INavigationService>(),
        Substitute.For<IDialogService>(),
        Substitute.For<IWindowManagerService>(),
        Substitute.For<IWindowTrackingService>(),
        Substitute.For<ILocalizationService>(),
        Substitute.For<IVoiceInputService>(),
        Substitute.For<Wpf.Ui.ISnackbarService>());
}
