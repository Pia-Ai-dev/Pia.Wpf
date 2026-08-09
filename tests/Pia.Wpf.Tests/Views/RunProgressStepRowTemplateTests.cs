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

/// <summary>A <c>DataTemplate</c>'s content is never in the logical tree until a container realizes it, so the
/// row is loaded into a throwaway <see cref="ItemsControl"/> for <c>AncestorType=ItemsControl</c> to resolve.</summary>
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

        // By declared ItemsSource path, never by index: the panel has three ItemsControls plus two nested ones.
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

    /// <summary>The declared <c>IsEnabled</c> path, never its resolved value: it defaults to <c>True</c>, so a
    /// value-only check on the Pending row would be vacuous either way.</summary>
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

    /// <summary><c>Visibility</c> defaults to <c>Visible</c>, so only the Collapsed direction is non-vacuous.</summary>
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

    /// <summary>Hidden while <c>IsEditing</c> is false, which is the default and so the non-vacuous direction.</summary>
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

    /// <summary>The Done row is where a value-only reading of <c>IsEnabled</c> is not vacuous, unlike the
    /// Pending row where True is the DP default.</summary>
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
                ctx.Vm.State = RunProgressState.Paused; // the group must be visible for disablement to be observable

                rowElement = (FrameworkElement)ctx.Parsed.ItemTemplate.LoadContent();
                rowElement.DataContext = DoneRow();
                host.Items.Add(rowElement);
                return 0;
            });
            WpfStaHost.Pump();

            // Read Count and every IsEnabled on the STA thread: cross-thread DP access throws, it does not race.
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

    /// <summary>Found as the Edit button's parent, never by index: the row has several StackPanels.</summary>
    private static FrameworkElement VerbGroupOf(FrameworkElement row)
    {
        var editButton = BindingPathWalker.FindLogical<ButtonBase>(row)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.EditStepCommand");
        return (FrameworkElement)LogicalTreeHelper.GetParent(editButton)!;
    }

    /// <summary>Found two levels above the Save button, which sits in its own StackPanel inside the editor.</summary>
    private static FrameworkElement EditorPanelOf(FrameworkElement row)
    {
        var saveButton = BindingPathWalker.FindLogical<ButtonBase>(row)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "DataContext.SaveStepEditCommand");
        var buttonRow = LogicalTreeHelper.GetParent(saveButton)!;
        return (FrameworkElement)LogicalTreeHelper.GetParent(buttonRow)!;
    }
}
