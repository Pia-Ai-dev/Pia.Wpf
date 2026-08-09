using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Everything in the jobs row is item-scoped inside a <c>DataTemplate</c>, so a throwaway <see cref="ItemsControl"/>
/// carrying the shipped template supplies the logical parent that <c>AncestorType=ItemsControl</c> resolves against.</summary>
[Collection("WpfApplicationStatic")]
public class ScheduledJobsRowTemplateTests
{
    private sealed class Ctx
    {
        public required ItemsControl Parsed { get; init; }
        public required ScheduledJobsSettingsViewModel Vm { get; init; }
    }

    private static Ctx Build()
    {
        var view = new Pia.Views.SettingsViews.AssistantView();

        // By declared ItemsSource path, never by index: ComboBox is an ItemsControl too, and a sibling
        // ItemsControl in this view has the same ancestor-command shape.
        var parsed = BindingPathWalker.FindLogical<ItemsControl>(view)
            .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
                ?.Path?.Path == "Jobs");

        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);

        var jobs = Substitute.For<IScheduledJobService>();
        jobs.GetAllAsync().Returns(Array.Empty<ScheduledJob>());
        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        var vm = new ScheduledJobsSettingsViewModel(
            jobs, Substitute.For<IScheduledJobRunner>(), providers, loc,
            NullLogger<SettingsViewModel>.Instance);

        return new Ctx { Parsed = parsed, Vm = vm };
    }

    private static ScheduledJobRow RowA() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Nightly digest",
        Query = "summarise today",
        Kind = ScheduledJobKind.AgentTask,
        KindLabel = "Agent task",
        Recurrence = RecurrenceType.Daily,
        RecurrenceLabel = "Daily",
        TimeOfDay = new TimeOnly(9, 0),
        NextFireAt = new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Local),
        Status = ScheduledJobStatus.Active,
        StatusLabel = "Active",
        StatusIsKnown = true,
        IsEnabled = true,
        ToggleLabel = "Disable",
        GrantedTools = string.Empty,
        QuietOnSuccess = false,
        RecentRunsSummary = "Last 5 runs: 4 ok, 1 failed",
        RecentRunsDetail = "02/08/2026 09:00 - Completed",
        OwnedByThisDevice = true,
    };

    private static ScheduledJobRow RowB() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Foreign job",
        Query = "not mine",
        Kind = ScheduledJobKind.Research,
        KindLabel = "Research",
        Recurrence = RecurrenceType.Once,
        RecurrenceLabel = "Once",
        TimeOfDay = new TimeOnly(7, 30),
        NextFireAt = new DateTime(2026, 8, 3, 7, 30, 0, DateTimeKind.Local),
        Status = (ScheduledJobStatus)7,
        StatusLabel = "Unknown (7)",
        StatusIsKnown = false,
        IsEnabled = false,
        ToggleLabel = "Enable",
        GrantedTools = string.Empty,
        QuietOnSuccess = false,
        // Empty drives HasRecentRuns -> Collapsed, the direction Visibility cannot reach by default.
        RecentRunsSummary = string.Empty,
        RecentRunsDetail = string.Empty,
        OwnedByThisDevice = false,
    };

    /// <summary>Descends logical AND visual, deduped, so a future template change that adds a generated visual child is found.</summary>
    private static IEnumerable<T> FindElements<T>(DependencyObject root) where T : DependencyObject
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current)) continue;
            if (current is T hit) yield return hit;

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                stack.Push(child);

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                    stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    [Fact]
    public void JobsRowTemplate_BindsEveryItemScopedPath_AcrossTwoRowsThatDiscriminate()
    {
        Ctx? ctx = null;
        FrameworkElement? rowElementA = null, rowElementB = null;
        var rowDataA = RowA();
        var rowDataB = RowB();

        (int ButtonCount, string? Name, string? Query, string? Kind, string? Recurrence, string? Status,
            string? NextFireAt, string NotOwnedVisibility, string? ToggleLabel, bool ToggleEnabled,
            bool RunNowEnabled, string? RecentRuns, string RecentRunsVisibility, string? RecentRunsToolTip) a, b;

        try
        {
            WpfStaHost.Run(() =>
            {
                ctx = Build();
                var host = new ItemsControl { ItemTemplate = ctx.Parsed.ItemTemplate, DataContext = ctx.Vm };

                rowElementA = (FrameworkElement)ctx.Parsed.ItemTemplate.LoadContent();
                rowElementA.DataContext = rowDataA;
                host.Items.Add(rowElementA);

                rowElementB = (FrameworkElement)ctx.Parsed.ItemTemplate.LoadContent();
                rowElementB.DataContext = rowDataB;
                host.Items.Add(rowElementB);
                return 0;
            });
            WpfStaHost.Pump();

            a = WpfStaHost.Run(() => Observe(rowElementA!));
            b = WpfStaHost.Run(() => Observe(rowElementB!));
        }
        finally
        {
            // Inert today: the ViewModel is not IDisposable. Kept for the day it gains a subscription.
            WpfStaHost.Run(() => { (ctx?.Vm as IDisposable)?.Dispose(); return 0; });
        }

        Assert.Equal(4, a.ButtonCount);
        Assert.Equal(4, b.ButtonCount);

        Assert.Equal("Nightly digest", a.Name);
        Assert.Equal("summarise today", a.Query);
        Assert.Equal("Agent task", a.Kind);
        Assert.Equal("Daily", a.Recurrence);
        Assert.Equal("Active", a.Status);
        Assert.Equal("Unknown (7)", b.Status);   // pins the unknown-status render too

        // WPF renders StringFormat=g with the element's Language (en-US here), not CurrentCulture, so the
        // expected string is derived with an explicit culture rather than written as a literal.
        Assert.Equal(rowDataA.NextFireAt.ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
            a.NextFireAt);

        // Visibility defaults to Visible, so row A's Collapsed is the only half the binding can have produced.
        Assert.Equal("Collapsed", a.NotOwnedVisibility);
        Assert.Equal("Visible", b.NotOwnedVisibility);

        Assert.Equal("Disable", a.ToggleLabel);
        Assert.Equal("Enable", b.ToggleLabel);

        // IsEnabled defaults to True, so row B's False is the only half the binding can have produced.
        Assert.True(a.ToggleEnabled);
        Assert.True(a.RunNowEnabled);
        Assert.False(b.ToggleEnabled);
        Assert.False(b.RunNowEnabled);

        Assert.Equal("Last 5 runs: 4 ok, 1 failed", a.RecentRuns);
        Assert.Equal("02/08/2026 09:00 - Completed", a.RecentRunsToolTip);
        Assert.Equal("Visible", a.RecentRunsVisibility);
        Assert.Equal("Collapsed", b.RecentRunsVisibility);
    }

    private static (int ButtonCount, string? Name, string? Query, string? Kind, string? Recurrence,
        string? Status, string? NextFireAt, string NotOwnedVisibility, string? ToggleLabel, bool ToggleEnabled,
        bool RunNowEnabled, string? RecentRuns, string RecentRunsVisibility, string? RecentRunsToolTip)
        Observe(FrameworkElement row)
    {
        var texts = FindElements<TextBlock>(row).ToList();
        var buttons = FindElements<ButtonBase>(row).ToList();

        string? TextOf(string path) =>
            texts.Single(tb => BindingPathWalker.PathOf(tb, TextBlock.TextProperty) == path).Text;

        var notOwned = texts.Single(tb => BindingPathWalker.PathOf(tb, UIElement.VisibilityProperty) == "OwnedByThisDevice");
        var recentRuns = texts.Single(tb => BindingPathWalker.PathOf(tb, UIElement.VisibilityProperty) == "HasRecentRuns");
        var toggle = buttons.Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.ToggleEnabledCommand");
        var runNow = buttons.Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.RunNowCommand");

        // Both fixture rows co-vary on all four booleans, so a value-only read cannot tell the two
        // IsEnabled bindings apart; assert the declared path instead.
        Assert.Equal("StatusIsKnown", BindingPathWalker.PathOf(toggle, UIElement.IsEnabledProperty));
        Assert.Equal("CanRunNow", BindingPathWalker.PathOf(runNow, UIElement.IsEnabledProperty));

        return (
            buttons.Count,
            TextOf("Name"),
            TextOf("Query"),
            TextOf("KindLabel"),
            TextOf("RecurrenceLabel"),
            TextOf("StatusLabel"),
            texts.Single(tb => BindingPathWalker.PathOf(tb, TextBlock.TextProperty) == "NextFireAt").Text,
            notOwned.Visibility.ToString(),
            toggle.Content?.ToString(),
            toggle.IsEnabled,
            runNow.IsEnabled,
            recentRuns.Text,
            recentRuns.Visibility.ToString(),
            recentRuns.ToolTip?.ToString());
    }

    [Fact]
    public void JobsRowTemplate_ResolvesAllFourAncestorCommands_ToTheInstanceOnTheViewModel()
    {
        // DeleteCommand has no other C# reference in the repo: only the toolkit's Async-suffix-stripping
        // naming convention keeps the Delete button's binding alive, so naming it here turns a rename into
        // a build break rather than a silently dead button.
        Ctx? ctx = null;
        FrameworkElement? rowElement = null;
        var rowData = RowA();
        (string Path, bool Identity, bool ParamIsRowData, string Status)[] probes;

        try
        {
            WpfStaHost.Run(() =>
            {
                ctx = Build();
                var host = new ItemsControl { ItemTemplate = ctx.Parsed.ItemTemplate, DataContext = ctx.Vm };

                rowElement = (FrameworkElement)ctx.Parsed.ItemTemplate.LoadContent();
                rowElement.DataContext = rowData;
                host.Items.Add(rowElement);
                return 0;
            });
            WpfStaHost.Pump();

            probes = WpfStaHost.Run(() => ProbeCommands(rowElement!, ctx!.Vm, rowData));
        }
        finally
        {
            WpfStaHost.Run(() => { (ctx?.Vm as IDisposable)?.Dispose(); return 0; });
        }

        Assert.Equal(4, probes.Length);
        foreach (var p in probes)
        {
            // Identity, not non-null: a non-null check cannot tell a resolved-but-wrong command from the right one.
            Assert.True(p.Identity,
                $"{p.Path} did not resolve to the SAME command instance on ScheduledJobsSettingsViewModel " +
                $"(BindingExpression.Status={p.Status}).");

            // Not evidence for the ancestor technique — {Binding} needs no ancestor — but the four buttons
            // must still deliver the right row to their command.
            Assert.True(p.ParamIsRowData, $"{p.Path}'s CommandParameter is not the row's ScheduledJobRow.");
        }

        // Named, not just counted, so a renamed path cannot shrink the set silently.
        Assert.Contains(probes, p => p.Path == "DataContext.StartEditCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.ToggleEnabledCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.RunNowCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.DeleteCommand");
    }

    private static (string Path, bool Identity, bool ParamIsRowData, string Status)[] ProbeCommands(
        FrameworkElement row, ScheduledJobsSettingsViewModel vm, ScheduledJobRow rowData) =>
        FindElements<ButtonBase>(row)
            .Select(b =>
            {
                var path = BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) ?? "<unbound>";
                var be = BindingOperations.GetBindingExpression(b, ButtonBase.CommandProperty);
                var expected = Expected(vm, path);
                return (path,
                    expected is not null && ReferenceEquals(b.Command, expected),
                    ReferenceEquals(b.CommandParameter, rowData),
                    be is null ? "<no expr>" : be.Status.ToString());
            })
            .ToArray();

    private static object? Expected(ScheduledJobsSettingsViewModel vm, string path) => path switch
    {
        "DataContext.StartEditCommand" => vm.StartEditCommand,
        "DataContext.ToggleEnabledCommand" => vm.ToggleEnabledCommand,
        "DataContext.RunNowCommand" => vm.RunNowCommand,
        "DataContext.DeleteCommand" => vm.DeleteCommand,
        _ => null,
    };
}
