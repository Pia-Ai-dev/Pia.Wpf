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

/// <summary>
/// Batch 09's scheduled-jobs row <c>DataTemplate</c> (<c>Views/SettingsViews/AssistantView.xaml:545</c>–<c>:603</c>,
/// the <c>DataTemplate</c> element itself <c>:547</c>–<c>:601</c>) — ten item-scoped binding paths plus four
/// <c>RelativeSource AncestorType=ItemsControl</c> command bindings that <see cref="BindingPathWalker"/> cannot
/// see (it is a LOGICAL walk over the non-templated tree; everything here lives inside a template, item-scoped).
/// <para>
/// <b>Batch 14 D1, decided (a):</b> a THROWAWAY <see cref="ItemsControl"/> carrying the parsed control's own
/// <c>ItemTemplate</c> and the real ViewModel as <c>DataContext</c>, with <c>ItemsSource</c> left unset so
/// <c>Items</c> stays usable. Adding a loaded row to <c>Items</c> makes the throwaway control its LOGICAL parent
/// immediately — no panel, no <c>ApplyTemplate</c>, no measure/arrange pass — and that parent is exactly what
/// <c>RelativeSource AncestorType=ItemsControl</c> resolves against. The parsed control is needed only as the
/// SOURCE of the template, so the shipped markup is what drives every assertion below, not a hand-built copy.
/// </para>
/// <para>
/// The FIFTH <c>AncestorType=ItemsControl</c> command binding in this file, the tool-permissions grants row's
/// <c>DataContext.RevokeCommand</c> at <c>AssistantView.xaml:221</c>, stays uncovered by this batch (W12).
/// </para>
/// </summary>
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

        // Identify the jobs ItemsControl by its DECLARED ItemsSource path, never by index: ComboBox is also
        // an ItemsControl, and this view has at least six, plus a second ItemsControl (tool-permission grants,
        // :183) with the SAME ancestor-command shape. .Single() is the non-vacuity guard.
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
        OwnedByThisDevice = false,
    };

    /// <summary>Logical AND visual descent, deduped: a <c>DataTemplate.LoadContent()</c>'d row is reachable
    /// logically; nothing in this row needs a visual pass, but the shape is kept identical to the one Ground D
    /// measured so a future template change that DOES introduce a generated visual child is still found.</summary>
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
            bool RunNowEnabled) a, b;

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
            // ScheduledJobsSettingsViewModel is not IDisposable (UiThreadViewModel : ObservableObject, no ctor
            // subscriptions), so this is inert today. Kept per hazard 4's shape for the day it gains one.
            // Batch 14 review D5, declined: this is a bounded WpfStaHost.Run inside a finally, so a wedged
            // dispatcher throws a SECOND TimeoutException here that C# `finally` semantics let REPLACE
            // whatever was already propagating from the try body above (the real stage, and its message,
            // is lost). Not fixed: the honest fix needs a `bodyFaulted` flag set from a catch that
            // rethrows and gates a swallow here, ~5 lines across 3 sites in this batch, which is bigger
            // than this nit's budget; a bare `catch` around this call was rejected because it would
            // silently drop a genuine disposal failure on an otherwise-passing test, which is worse than
            // the message it would be closing.
            WpfStaHost.Run(() => { (ctx?.Vm as IDisposable)?.Dispose(); return 0; });
        }

        // Non-vacuity for the fact as a whole: the template's four ButtonBases were found at all, on both rows.
        Assert.Equal(4, a.ButtonCount);
        Assert.Equal(4, b.ButtonCount);

        // Paths 1-6: plain TextBlock.Text. An unbound Text is "", never the row's real value, so any of these
        // matching row A's data is non-vacuous on its own.
        Assert.Equal("Nightly digest", a.Name);
        Assert.Equal("summarise today", a.Query);
        Assert.Equal("Agent task", a.Kind);
        Assert.Equal("Daily", a.Recurrence);
        Assert.Equal("Active", a.Status);
        Assert.Equal("Unknown (7)", b.Status);   // pins the unknown-status render too

        // Path 6, NextFireAt: DERIVED from the row's own value with an explicit en-US culture (hazard 12), never
        // the literal "8/2/2026 9:00 AM" — WPF renders StringFormat=g using the element's Language (default
        // en-US: no xml:lang/Language= anywhere in AssistantView.xaml), while a test using ToString("g") with no
        // culture argument would use CurrentCulture and could differ on a non-en-US box.
        Assert.Equal(rowDataA.NextFireAt.ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
            a.NextFireAt);

        // Path 7, OwnedByThisDevice -> TextBlock.Visibility (inverse converter). Visibility defaults to
        // Visible (hazard 8), so the direction that can ONLY be reached through the binding is row A's
        // Collapsed (OwnedByThisDevice=true). Row B's Visible is the VACUOUS half of this path — asserted for
        // symmetry only, and said so here rather than left silent.
        Assert.Equal("Collapsed", a.NotOwnedVisibility);
        Assert.Equal("Visible", b.NotOwnedVisibility);

        // Path 8, ToggleLabel -> ui:Button.Content (not a TextBlock; no template applied here, so read as a
        // plain object). Content defaults to null, so either value matching is non-vacuous.
        Assert.Equal("Disable", a.ToggleLabel);
        Assert.Equal("Enable", b.ToggleLabel);

        // Paths 9-10, StatusIsKnown / CanRunNow -> Button.IsEnabled. IsEnabled defaults to True (hazard 8), so
        // row A's all-true reading is the VACUOUS half (asserted below for symmetry); row B is the direction
        // that can only be reached through the binding: StatusIsKnown=false and OwnedByThisDevice=false (so
        // CanRunNow = OwnedByThisDevice && StatusIsKnown is false too) drive both buttons to False.
        Assert.True(a.ToggleEnabled);
        Assert.True(a.RunNowEnabled);
        Assert.False(b.ToggleEnabled);
        Assert.False(b.RunNowEnabled);
    }

    private static (int ButtonCount, string? Name, string? Query, string? Kind, string? Recurrence,
        string? Status, string? NextFireAt, string NotOwnedVisibility, string? ToggleLabel, bool ToggleEnabled,
        bool RunNowEnabled) Observe(FrameworkElement row)
    {
        var texts = FindElements<TextBlock>(row).ToList();
        var buttons = FindElements<ButtonBase>(row).ToList();

        string? TextOf(string path) =>
            texts.Single(tb => BindingPathWalker.PathOf(tb, TextBlock.TextProperty) == path).Text;

        var notOwned = texts.Single(tb => BindingPathWalker.PathOf(tb, UIElement.VisibilityProperty) == "OwnedByThisDevice");
        var toggle = buttons.Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.ToggleEnabledCommand");
        var runNow = buttons.Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.RunNowCommand");

        // D2 (Batch 14 review): paths 9-10 were located above by their Command path and then read only by
        // VALUE, and both fixture rows co-vary on StatusIsKnown/CanRunNow/OwnedByThisDevice/IsEnabled, so any
        // of the four booleans was interchangeable with any other in either IsEnabled slot -- a swap at
        // AssistantView.xaml:587<->:592 kept the value-only asserts below green. Assert the declared
        // IsEnabled binding PATH itself, never by index or by the value it happens to resolve to.
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
            runNow.IsEnabled);
    }

    [Fact]
    public void JobsRowTemplate_ResolvesAllFourAncestorCommands_ToTheInstanceOnTheViewModel()
    {
        // DeleteCommand is the ONLY one of the four with zero C# references anywhere in the repo (the other
        // three are protected by compiled call sites: ScheduledJobsSettingsViewModelTests.cs:79, :111, :169).
        // Nothing but CommunityToolkit's Async-suffix-stripping naming convention keeps AssistantView.xaml:596
        // alive -- renaming DeleteAsync (ScheduledJobsSettingsViewModel.cs:376) breaks the Delete button
        // silently, at 0 warnings, with every ViewModel test green. After this fact, it breaks the BUILD,
        // because the fact names vm.DeleteCommand directly -- a harder stop than a red test.
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
            // Batch 14 review D5, declined: see the sibling finally in
            // JobsRowTemplate_BindsEveryItemScopedPath_AcrossTwoRowsThatDiscriminate above for why this
            // bounded Run is left as-is rather than guarded against masking an in-flight exception.
            WpfStaHost.Run(() => { (ctx?.Vm as IDisposable)?.Dispose(); return 0; });
        }

        Assert.Equal(4, probes.Length);
        foreach (var p in probes)
        {
            // Command IDENTITY is the only thing that proves the ancestor technique -- a plain non-null check
            // does not discriminate a resolved-but-wrong command from the right one.
            Assert.True(p.Identity,
                $"{p.Path} did not resolve to the SAME command instance on ScheduledJobsSettingsViewModel " +
                $"(BindingExpression.Status={p.Status}).");

            // CommandParameter is NOT evidence for the ancestor technique: {Binding} resolves off the row's own
            // DataContext and needs no ancestor at all, so this was true in the D1 null-parent control too.
            // It is asserted anyway because the four buttons must still deliver the right row to their command.
            Assert.True(p.ParamIsRowData, $"{p.Path}'s CommandParameter is not the row's ScheduledJobRow.");
        }

        // A renamed path cannot shrink the set silently: each of the four is named, not just counted.
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
