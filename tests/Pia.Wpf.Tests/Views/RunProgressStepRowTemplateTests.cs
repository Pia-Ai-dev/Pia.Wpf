using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Batch 08 8b's step-row <c>DataTemplate</c> (<c>Controls/Assistant/RunProgressPanel.xaml</c>, the Steps
/// <c>ItemsControl</c>'s <c>ItemTemplate</c>) — the five plan-mutation verbs (Edit/Insert/MoveUp/MoveDown/
/// Skip) plus the inline editor's Save/Cancel, all item-scoped and therefore invisible to
/// <see cref="RunProgressPanelParseTests"/>'s LOGICAL walk over the non-templated tree (a
/// <c>DataTemplate</c>'s content is never in the logical tree until a container realizes it).
/// <para>
/// Follows <see cref="ScheduledJobsRowTemplateTests"/> character for character (hazard 11): a THROWAWAY
/// <see cref="ItemsControl"/> carrying the parsed panel's own <c>ItemTemplate</c> and a real
/// <see cref="RunProgressViewModel"/> as <c>DataContext</c>, with a loaded row added to <c>Items</c> so
/// <c>RelativeSource AncestorType=ItemsControl</c> resolves against it — no panel, no
/// <c>ApplyTemplate</c>, no measure/arrange pass. The parsed control is needed only as the SOURCE of the
/// template, so the shipped markup drives every assertion below, not a hand-built copy.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class RunProgressStepRowTemplateTests
{
    private sealed class Ctx
    {
        public required ItemsControl Parsed { get; init; }
        public required RunProgressViewModel Vm { get; init; }
    }

    private static Ctx Build()
    {
        var panel = new RunProgressPanel();

        // Identify the Steps ItemsControl by its DECLARED ItemsSource path, never by index: the panel has
        // three ItemsControls (Steps, Timeline, Children) plus two more nested inside the Children template.
        var parsed = BindingPathWalker.FindLogical<ItemsControl>(panel)
            .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
                ?.Path?.Path == "Steps");

        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);

        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentRun?)null);

        var vm = new RunProgressViewModel(
            runs, Guid.NewGuid(), loc, Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);

        return new Ctx { Parsed = parsed, Vm = vm };
    }

    private static StepRowViewModel PendingRow() => new()
    {
        StepId = Guid.NewGuid(),
        Title = "Draft the release summary",
        Status = AgentStepStatus.Pending,
    };

    private static StepRowViewModel DoneRow() => new()
    {
        StepId = Guid.NewGuid(),
        Title = "Already finished",
        Status = AgentStepStatus.Done,
    };

    /// <summary>
    /// The five plan-mutation verb buttons, asserted per <see cref="ScheduledJobsRowTemplateTests"/>'s own
    /// discipline: the command PATH, command IDENTITY (never merely non-null), <c>CommandParameter</c> is the
    /// row instance, and the declared <c>IsEnabled</c> PATH — never its resolved value (hazard 12: a
    /// <c>Button.IsEnabled</c> defaults to <c>True</c>, so a value-only check on the Pending row would be
    /// vacuous either way).
    /// </summary>
    [Fact]
    public void FiveVerbButtons_ResolveToTheirCommands_WithTheRowAsParameter_AndIsEnabledBoundToIsMutable()
    {
        Ctx? ctx = null;
        FrameworkElement? rowElement = null;
        var rowData = PendingRow();
        (string Path, bool Identity, bool ParamIsRowData, string? IsEnabledPath)[] probes;

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

            probes = WpfStaHost.Run(() => ProbeVerbButtons(rowElement!, ctx!.Vm, rowData));
        }
        finally
        {
            WpfStaHost.Run(() => { ctx?.Vm.Dispose(); return 0; });
        }

        Assert.Equal(5, probes.Length);
        foreach (var p in probes)
        {
            Assert.True(p.Identity,
                $"{p.Path} did not resolve to the SAME command instance on RunProgressViewModel.");
            Assert.True(p.ParamIsRowData, $"{p.Path}'s CommandParameter is not the row's StepRowViewModel.");
            Assert.Equal("IsMutable", p.IsEnabledPath);
        }

        Assert.Contains(probes, p => p.Path == "DataContext.EditStepCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.InsertStepBelowCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.MoveStepUpCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.MoveStepDownCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.SkipStepCommand");
    }

    /// <summary>
    /// The row-button group's OWN visibility — bound to <c>DataContext.CanMutatePlan</c> off the ancestor
    /// <c>ItemsControl</c> (the VM), never to anything on the row — is <c>Collapsed</c> BEFORE the mutation
    /// (hazard 8: <c>Visibility</c> defaults to <c>Visible</c>, so only the Collapsed direction is
    /// non-vacuous), and <c>Visible</c> once the run is <see cref="RunProgressState.Paused"/>.
    /// </summary>
    [Fact]
    public void VerbButtonGroup_IsHiddenUntilTheRunIsPaused()
    {
        Ctx? ctx = null;
        FrameworkElement? rowElement = null;
        Visibility before, after;

        try
        {
            WpfStaHost.Run(() =>
            {
                ctx = Build();
                var host = new ItemsControl { ItemTemplate = ctx.Parsed.ItemTemplate, DataContext = ctx.Vm };

                rowElement = (FrameworkElement)ctx.Parsed.ItemTemplate.LoadContent();
                rowElement.DataContext = PendingRow();
                host.Items.Add(rowElement);
                return 0;
            });
            WpfStaHost.Pump();

            before = WpfStaHost.Run(() => VerbGroupOf(rowElement!).Visibility);

            WpfStaHost.Run(() => { ctx!.Vm.State = RunProgressState.Paused; return 0; });
            WpfStaHost.Pump();

            after = WpfStaHost.Run(() => VerbGroupOf(rowElement!).Visibility);
        }
        finally
        {
            WpfStaHost.Run(() => { ctx?.Vm.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Collapsed, before);
        Assert.Equal(Visibility.Visible, after);
    }

    /// <summary>
    /// The inline editor: hidden while <c>IsEditing</c> is false (the default, so this direction is the
    /// non-vacuous one — hazard 8), and its Save/Cancel buttons resolve to the SAME commands the header verbs
    /// use the identical ancestor pattern for.
    /// </summary>
    [Fact]
    public void InlineEditor_IsHiddenByDefault_AndItsButtonsResolveToSaveAndCancel()
    {
        Ctx? ctx = null;
        FrameworkElement? rowElement = null;
        var rowData = PendingRow();
        Visibility editorVisibilityBefore;
        (string Path, bool Identity, bool ParamIsRowData)[] probes;

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

            editorVisibilityBefore = WpfStaHost.Run(() => EditorPanelOf(rowElement!).Visibility);

            probes = WpfStaHost.Run(() => ProbeEditorButtons(rowElement!, ctx!.Vm, rowData));
        }
        finally
        {
            WpfStaHost.Run(() => { ctx?.Vm.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Collapsed, editorVisibilityBefore);

        Assert.Equal(2, probes.Length);
        foreach (var p in probes)
        {
            Assert.True(p.Identity, $"{p.Path} did not resolve to the SAME command instance.");
            Assert.True(p.ParamIsRowData, $"{p.Path}'s CommandParameter is not the row's StepRowViewModel.");
        }
        Assert.Contains(probes, p => p.Path == "DataContext.SaveStepEditCommand");
        Assert.Contains(probes, p => p.Path == "DataContext.CancelStepEditCommand");
    }

    /// <summary>Both fixture rows discriminate <see cref="StepRowViewModel.IsMutable"/> at the template
    /// level, not just on the model — the Done row's five buttons are all present but every one is
    /// individually disabled, which the value-only reading of <c>IsEnabled</c> below is not vacuous for
    /// (unlike the Pending row, where True is the DP default).</summary>
    [Fact]
    public void ADoneRow_HasAllFiveButtonsDisabled_ThroughIsMutable()
    {
        Ctx? ctx = null;
        FrameworkElement? rowElement = null;

        try
        {
            WpfStaHost.Run(() =>
            {
                ctx = Build();
                var host = new ItemsControl { ItemTemplate = ctx.Parsed.ItemTemplate, DataContext = ctx.Vm };
                ctx.Vm.State = RunProgressState.Paused; // the group must be VISIBLE, so disablement is observable

                rowElement = (FrameworkElement)ctx.Parsed.ItemTemplate.LoadContent();
                rowElement.DataContext = DoneRow();
                host.Items.Add(rowElement);
                return 0;
            });
            WpfStaHost.Pump();

            // Read Count AND every IsEnabled on the STA thread itself — a Button created there cannot be
            // touched from the test's own thread (hazard 4's sibling: cross-thread DP access throws, it does
            // not merely race).
            var (count, allDisabled) = WpfStaHost.Run(() =>
            {
                var buttons = ProbeVerbButtonElements(rowElement!);
                return (buttons.Length, buttons.All(b => !b.IsEnabled));
            });

            Assert.Equal(5, count);
            Assert.True(allDisabled, "at least one of the Done row's five verb buttons was still enabled");
        }
        finally
        {
            WpfStaHost.Run(() => { ctx?.Vm.Dispose(); return 0; });
        }
    }

    private static ButtonBase[] ProbeVerbButtonElements(FrameworkElement row) =>
        BindingPathWalker.FindLogical<ButtonBase>(row)
            .Where(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) is
                "DataContext.EditStepCommand" or "DataContext.InsertStepBelowCommand" or
                "DataContext.MoveStepUpCommand" or "DataContext.MoveStepDownCommand" or
                "DataContext.SkipStepCommand")
            .ToArray();

    private static (string Path, bool Identity, bool ParamIsRowData, string? IsEnabledPath)[] ProbeVerbButtons(
        FrameworkElement row, RunProgressViewModel vm, StepRowViewModel rowData) =>
        ProbeVerbButtonElements(row)
            .Select(b =>
            {
                var path = BindingPathWalker.PathOf(b, ButtonBase.CommandProperty)!;
                var expected = ExpectedVerb(vm, path);
                return (path,
                    ReferenceEquals(b.Command, expected),
                    ReferenceEquals(b.CommandParameter, rowData),
                    BindingPathWalker.PathOf(b, UIElement.IsEnabledProperty));
            })
            .ToArray();

    private static (string Path, bool Identity, bool ParamIsRowData)[] ProbeEditorButtons(
        FrameworkElement row, RunProgressViewModel vm, StepRowViewModel rowData) =>
        BindingPathWalker.FindLogical<ButtonBase>(row)
            .Where(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) is
                "DataContext.SaveStepEditCommand" or "DataContext.CancelStepEditCommand")
            .Select(b =>
            {
                var path = BindingPathWalker.PathOf(b, ButtonBase.CommandProperty)!;
                var expected = path == "DataContext.SaveStepEditCommand"
                    ? (object)vm.SaveStepEditCommand
                    : vm.CancelStepEditCommand;
                return (path, ReferenceEquals(b.Command, expected), ReferenceEquals(b.CommandParameter, rowData));
            })
            .ToArray();

    private static object? ExpectedVerb(RunProgressViewModel vm, string path) => path switch
    {
        "DataContext.EditStepCommand" => vm.EditStepCommand,
        "DataContext.InsertStepBelowCommand" => vm.InsertStepBelowCommand,
        "DataContext.MoveStepUpCommand" => vm.MoveStepUpCommand,
        "DataContext.MoveStepDownCommand" => vm.MoveStepDownCommand,
        "DataContext.SkipStepCommand" => vm.SkipStepCommand,
        _ => null,
    };

    /// <summary>The StackPanel wrapping the five verb buttons — identified by being the parent of the Edit
    /// button, never by index (the row has several StackPanels).</summary>
    private static FrameworkElement VerbGroupOf(FrameworkElement row)
    {
        var editButton = BindingPathWalker.FindLogical<ButtonBase>(row)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.EditStepCommand");
        return (FrameworkElement)LogicalTreeHelper.GetParent(editButton)!;
    }

    /// <summary>The StackPanel wrapping the inline editor — identified by being the parent of the Save
    /// button's own parent (Save/Cancel sit in their own horizontal StackPanel one level below the editor's
    /// outer one), never by index.</summary>
    private static FrameworkElement EditorPanelOf(FrameworkElement row)
    {
        var saveButton = BindingPathWalker.FindLogical<ButtonBase>(row)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.SaveStepEditCommand");
        var buttonRow = LogicalTreeHelper.GetParent(saveButton)!;
        return (FrameworkElement)LogicalTreeHelper.GetParent(buttonRow)!;
    }
}
