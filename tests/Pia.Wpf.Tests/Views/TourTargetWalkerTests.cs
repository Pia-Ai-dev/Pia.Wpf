using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Pia.Helpers;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

[Collection("WpfApplicationStatic")]
public class TourTargetWalkerTests
{
    [Fact]
    public void OnlyElementsWithAnAutomationId_AreOffered()
    {
        var ids = WpfStaHost.Run(() =>
        {
            var root = new StackPanel();
            root.Children.Add(Id(Box(), "First"));
            root.Children.Add(Box());
            root.Children.Add(Id(Box(), "Third"));

            return Scan(root).Targets.Select(t => t.AutomationId).ToList();
        });

        Assert.Equal(new[] { "First", "Third" }, ids);
    }

    [Theory]
    [InlineData(Visibility.Collapsed)]
    [InlineData(Visibility.Hidden)]
    public void ACollapsedOrHiddenContainer_TakesItsSubtreeOutOfTheOffer(Visibility visibility)
    {
        var ids = WpfStaHost.Run(() =>
        {
            var hidden = new StackPanel { Visibility = visibility };
            hidden.Children.Add(Id(Box(), "Hidden"));

            var root = new StackPanel();
            root.Children.Add(hidden);
            root.Children.Add(Id(Box(), "Sibling"));

            return Scan(root).Targets.Select(t => t.AutomationId).ToList();
        });

        Assert.Equal(new[] { "Sibling" }, ids);
    }

    /// <summary>The per-row delete button of the chat history is hidden this way until hover.</summary>
    [Fact]
    public void AHoverRevealedControl_IsOfferedOnlyOnceItIsOpaqueAndHitTestable()
    {
        var (whileHidden, afterHover) = WpfStaHost.Run(() =>
        {
            var reveal = new Border
            {
                Opacity = 0,
                IsHitTestVisible = false,
                Child = Id(Box(), "Delete"),
            };

            var root = new Grid();
            root.Children.Add(reveal);

            var before = Scan(root).Targets.Count;

            reveal.Opacity = 1;
            reveal.IsHitTestVisible = true;

            return (before, Scan(root).Targets.Count);
        });

        Assert.Equal(0, whileHidden);
        Assert.Equal(1, afterHover);
    }

    /// <summary>A greyed-out Save is exactly the thing a tour explains, so "hit-testable" is not "enabled".</summary>
    [Fact]
    public void ADisabledControl_IsStillOffered()
    {
        var ids = WpfStaHost.Run(() =>
        {
            var root = new Grid();
            root.Children.Add(Id(new Button { Content = "Save", Width = 100, Height = 30, IsEnabled = false }, "Save"));

            return Scan(root).Targets.Select(t => t.AutomationId).ToList();
        });

        Assert.Equal(new[] { "Save" }, ids);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(3d)]
    public void AZeroOrHairlineSizedControl_IsNotOffered(double size)
    {
        var ids = WpfStaHost.Run(() =>
        {
            var root = new Canvas();
            root.Children.Add(Id(new Border { Width = size, Height = size }, "Hairline"));

            var big = Id(Box(), "Big");
            Canvas.SetTop(big, 40);
            root.Children.Add(big);

            return Scan(root).Targets.Select(t => t.AutomationId).ToList();
        });

        Assert.Equal(new[] { "Big" }, ids);
    }

    [Fact]
    public void BoundsAreReportedRelativeToTheRoot()
    {
        var bounds = WpfStaHost.Run(() =>
        {
            var placed = Id(Box(), "Placed");
            Canvas.SetLeft(placed, 50);
            Canvas.SetTop(placed, 20);

            var root = new Canvas();
            root.Children.Add(placed);

            return Scan(root).Targets.Single().Bounds;
        });

        Assert.Equal(new TourTargetBounds(50, 20, 100, 30), bounds);
    }

    [Fact]
    public void AControlScrolledOutOfItsViewport_IsNotOffered()
    {
        var (beforeScroll, afterScroll, offsetY) = WpfStaHost.Run(() =>
        {
            var content = new StackPanel();
            for (var i = 0; i < 20; i++)
                content.Children.Add(new Border { Height = 40 });
            content.Children.Add(Id(Box(), "BelowTheFold"));

            var scroller = new ScrollViewer
            {
                Width = 400,
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            };

            var root = new Grid();
            root.Children.Add(scroller);

            var before = Scan(root, 400, 100).Targets.Count;

            scroller.ScrollToVerticalOffset(10_000);
            Scan(root, 400, 100);

            var after = Scan(root, 400, 100).Targets;
            return (before, after.Count, after.Count == 1 ? after[0].Bounds.Y : double.NaN);
        });

        Assert.Equal(0, beforeScroll);
        Assert.Equal(1, afterScroll);
        Assert.InRange(offsetY, 0, 100);
    }

    [Fact]
    public void AControlOutsideTheRootBounds_IsNotOffered()
    {
        var ids = WpfStaHost.Run(() =>
        {
            var offscreen = Id(Box(), "Offscreen");
            Canvas.SetLeft(offscreen, 600);

            var root = new Canvas();
            root.Children.Add(offscreen);
            root.Children.Add(Id(Box(), "Onscreen"));

            return Scan(root).Targets.Select(t => t.AutomationId).ToList();
        });

        Assert.Equal(new[] { "Onscreen" }, ids);
    }

    /// <summary>Outermost, because that is the one a navigation step can map back to a ViewModel.</summary>
    [Fact]
    public void TheOwningView_IsTheOutermostUserControlBelowTheRoot()
    {
        var (nested, chrome, rootView) = WpfStaHost.Run(() =>
        {
            var outer = new OuterTestView
            {
                Content = new InnerTestView { Content = Id(Box(), "Nested") },
            };

            var root = new StackPanel();
            root.Children.Add(outer);
            root.Children.Add(Id(Box(), "Chrome"));

            var scan = Scan(root);
            return (
                scan.Targets.Single(t => t.AutomationId == "Nested").OwningView,
                scan.Targets.Single(t => t.AutomationId == "Chrome").OwningView,
                scan.RootView);
        });

        Assert.Equal(nameof(OuterTestView), nested);
        Assert.Equal(nameof(StackPanel), chrome);
        Assert.Equal(nameof(StackPanel), rootView);
    }

    [Fact]
    public void TheOffer_IsCappedAndSaysSo()
    {
        var (capped, cappedFirstId, wasTruncated, underCap, underCapTruncated) = WpfStaHost.Run(() =>
        {
            var over = Tiles(TourTargetWalker.MaxTargets + 5);
            var overScan = Scan(over, 400, 300);

            var under = Tiles(TourTargetWalker.MaxTargets - 1);
            var underScan = Scan(under, 400, 300);

            return (
                overScan.Targets.Count,
                overScan.Targets[0].AutomationId,
                overScan.Truncated,
                underScan.Targets.Count,
                underScan.Truncated);
        });

        Assert.Equal(TourTargetWalker.MaxTargets, capped);
        Assert.Equal("Tile0", cappedFirstId);
        Assert.True(wasTruncated);
        Assert.Equal(TourTargetWalker.MaxTargets - 1, underCap);
        Assert.False(underCapTruncated);
    }

    /// <summary>Exactly at the cap nothing was dropped, so saying "truncated" would send the reader hunting for
    /// offers that do not exist.</summary>
    [Fact]
    public void AnOfferThatFillsTheCapExactly_IsNotTruncated()
    {
        var (count, truncated) = WpfStaHost.Run(() =>
        {
            var scan = Scan(Tiles(TourTargetWalker.MaxTargets), 400, 300);
            return (scan.Targets.Count, scan.Truncated);
        });

        Assert.Equal(TourTargetWalker.MaxTargets, count);
        Assert.False(truncated);
    }

    /// <summary>An id can legitimately repeat; hiding the ambiguity is worse than reporting it.</summary>
    [Fact]
    public void DuplicateIds_AreBothOffered()
    {
        var ids = WpfStaHost.Run(() =>
        {
            var root = new StackPanel();
            root.Children.Add(Id(Box(), "Tool_Revoke"));
            root.Children.Add(Id(Box(), "Tool_Revoke"));

            return Scan(root).Targets.Select(t => t.AutomationId).ToList();
        });

        Assert.Equal(new[] { "Tool_Revoke", "Tool_Revoke" }, ids);
    }

    /// <summary>The dump has to speak the playbook's <c>type=</c> language, not WPF's CLR names.</summary>
    [Fact]
    public void TheControlType_IsTheUiaVocabulary()
    {
        var types = WpfStaHost.Run(() =>
        {
            var root = new StackPanel();
            root.Children.Add(Id(new Button { Content = "go", Width = 100, Height = 30 }, "AButton"));
            root.Children.Add(Id(new TextBlock { Text = "hello", Width = 100, Height = 20 }, "AText"));
            root.Children.Add(Id(new Border { Width = 100, Height = 20 }, "ABorder"));

            return Scan(root).Targets.Select(t => t.ControlType).ToList();
        });

        Assert.Equal(new[] { "Button", "Text", "Border" }, types);
    }

    [Fact]
    public void TheName_PrefersTheExplicitAutomationName()
    {
        var (named, unnamed) = WpfStaHost.Run(() =>
        {
            var explicitly = Id(new Button { Content = "Save", Width = 100, Height = 30 }, "Explicit");
            AutomationProperties.SetName(explicitly, "Join a meeting");

            var root = new StackPanel();
            root.Children.Add(explicitly);
            root.Children.Add(Id(new Button { Content = "Save", Width = 100, Height = 30 }, "Implicit"));

            var scan = Scan(root);
            return (
                scan.Targets.Single(t => t.AutomationId == "Explicit").Name,
                scan.Targets.Single(t => t.AutomationId == "Implicit").Name);
        });

        Assert.Equal("Join a meeting", named);
        Assert.Equal("Save", unnamed);
    }

    /// <summary>An id interpolates typed keywords and a Name is a todo title, so neither may reach a format string.</summary>
    [Fact]
    public void TheRecordsToString_CarriesNeitherIdNorName()
    {
        var target = new TourTarget(
            "Settings_General_RemoveKeyword_hunter2",
            "Buy milk for Anna",
            "Button",
            new TourTargetBounds(1, 2, 3, 4),
            "SettingsView");
        var scan = new TourTargetScan("MainWindow", false, [target]);

        foreach (var rendered in new[] { target.ToString(), scan.ToString() })
        {
            Assert.DoesNotContain("hunter2", rendered);
            Assert.DoesNotContain("Anna", rendered);
        }
    }

    private static TourTargetScan Scan(FrameworkElement root, double width = 400, double height = 300)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return TourTargetWalker.Collect(root);
    }

    private static Canvas Tiles(int count)
    {
        var root = new Canvas();
        for (var i = 0; i < count; i++)
        {
            var tile = Id(new Border { Width = 10, Height = 10 }, $"Tile{i}");
            Canvas.SetLeft(tile, i % 20 * 10);
            Canvas.SetTop(tile, i / 20 * 10);
            root.Children.Add(tile);
        }
        return root;
    }

    // A Border, not a Button: WPF-UI's implicit Button style can impose a MinWidth/MinHeight and these
    // bodies assert exact rects.
    private static Border Box() => new() { Width = 100, Height = 30 };

    private static T Id<T>(T element, string automationId) where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(element, automationId);
        return element;
    }

    private sealed class OuterTestView : UserControl
    {
    }

    private sealed class InnerTestView : UserControl
    {
    }
}
