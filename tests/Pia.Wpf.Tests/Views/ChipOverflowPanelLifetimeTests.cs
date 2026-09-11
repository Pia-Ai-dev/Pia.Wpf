using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Controls.Chat;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The chip collections live on the AssistantMessage, which outlives every view built over it. A
/// CollectionChanged subscription left behind on discard roots the panel, its item container and the
/// whole message list — one navigation away from a long chat leaked ~140 MB that way.
/// A liveness assertion that ever flakes wants a short GC retry loop, not a Skip.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ChipOverflowPanelLifetimeTests
{
    [Fact]
    public void PanelDroppedFromTheTreeIsCollectedWhileItsItemsLive()
    {
        // Declared out here so it outlives the panel, which is the condition the leak needs.
        var items = new ObservableCollection<string> { "a", "b" };
        WeakReference? weak = null;
        Window? window = null;
        ContentPresenter? presenter = null;
        Button? parking = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                (window, presenter, parking) = Host();
                var panel = new PiaChipOverflowPanel { ItemsSource = items };
                presenter.Content = panel;
                window.UpdateLayout();
                weak = new WeakReference(panel);
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                // The focused element is rooted by its window, so a panel holding focus would look
                // leaked for a reason that has nothing to do with the subscription.
                Keyboard.Focus(parking);
                presenter!.Content = null;
                window!.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            WpfStaHost.Run(() => { window?.Close(); return 0; });
        }

        Assert.False(weak!.IsAlive, "the discarded panel is still rooted by the collection it was showing");
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void PanelStopsFollowingItsItemsWhileUnloadedAndCatchesUpOnReload()
    {
        var items = new ObservableCollection<string> { "first" };
        object? whileUnloaded = null;
        object? afterReload = null;
        Window? window = null;
        ContentPresenter? presenter = null;
        PiaChipOverflowPanel? panel = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                (window, presenter, _) = Host();
                panel = new PiaChipOverflowPanel { ItemsSource = items };
                presenter.Content = panel;
                window.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                presenter!.Content = null;
                window!.UpdateLayout();
                return 0;
            });
            // Unloaded is broadcast from a posted operation, so it has not run until the queue drains.
            WpfStaHost.Pump();

            whileUnloaded = WpfStaHost.Run(() =>
            {
                items[0] = "second";
                return panel!.Slot1;
            });

            WpfStaHost.Run(() =>
            {
                presenter!.Content = panel;
                window!.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            afterReload = WpfStaHost.Run(() => panel!.Slot1);
        }
        finally
        {
            WpfStaHost.Run(() => { window?.Close(); return 0; });
        }

        Assert.Equal("first", whileUnloaded);
        // Re-observing alone would leave the slot showing what it held when it was unloaded.
        Assert.Equal("second", afterReload);
    }

    /// <summary>Container recycling re-points a live panel at another message's chips, so the swap has to
    /// move the subscription with it.</summary>
    [Fact]
    public void PanelSwappedOntoAnotherCollectionFollowsTheNewOneOnly()
    {
        var first = new ObservableCollection<string> { "old" };
        var second = new ObservableCollection<string> { "new" };
        object? afterSwap = null;
        object? afterBothChange = null;
        Window? window = null;
        PiaChipOverflowPanel? panel = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                (window, var presenter, _) = Host();
                panel = new PiaChipOverflowPanel { ItemsSource = first };
                presenter.Content = panel;
                window.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            afterSwap = WpfStaHost.Run(() =>
            {
                panel!.ItemsSource = second;
                return panel.Slot1;
            });

            afterBothChange = WpfStaHost.Run(() =>
            {
                first.Insert(0, "old-later");
                second.Insert(0, "new-later");
                return panel!.Slot1;
            });
        }
        finally
        {
            WpfStaHost.Run(() => { window?.Close(); return 0; });
        }

        Assert.Equal("new", afterSwap);
        // "old-later" here would mean the panel is still listening to the collection it left behind.
        Assert.Equal("new-later", afterBothChange);
    }

    /// <summary>A real window, so WPF broadcasts Loaded/Unloaded the way a navigation does — raising
    /// them by hand reaches only the element they are raised on.</summary>
    private static (Window Window, ContentPresenter Presenter, Button Parking) Host()
    {
        var presenter = new ContentPresenter();
        var parking = new Button { Content = "park" };
        var grid = new Grid();
        grid.Children.Add(presenter);
        grid.Children.Add(parking);

        var window = new Window
        {
            Content = grid,
            Width = 600,
            Height = 400,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        return (window, presenter, parking);
    }
}
