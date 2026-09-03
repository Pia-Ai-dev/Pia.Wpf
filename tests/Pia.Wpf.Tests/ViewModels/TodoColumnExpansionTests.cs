using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.Views;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The Closed column starts collapsed and is rebuilt on every reload, so its expansion is the one
/// piece of board state a reload can silently throw away.
/// </summary>
public sealed class TodoColumnExpansionTests
{
    private static readonly KanbanColumn Todo = new()
    {
        Id = Guid.NewGuid(),
        Name = "To do",
        SortOrder = 0,
        IsDefaultView = true,
    };

    private static readonly KanbanColumn Closed = new()
    {
        Id = Guid.NewGuid(),
        Name = "Closed",
        SortOrder = 99,
        IsClosedColumn = true,
    };

    private static TodoViewModel Build()
    {
        var columns = Substitute.For<IKanbanColumnService>();
        columns.GetAllAsync().Returns(Task.FromResult<IReadOnlyList<KanbanColumn>>([Todo, Closed]));

        var todos = Substitute.For<ITodoService>();
        todos.GetCompletedTodayAsync().Returns(Task.FromResult<IReadOnlyList<TodoItem>>([]));
        todos.GetByColumnAsync(Arg.Any<Guid>()).Returns(Task.FromResult<IReadOnlyList<TodoItem>>([]));

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(Task.FromResult(new AppSettings()));

        return new TodoViewModel(
            NullLogger<TodoViewModel>.Instance,
            todos,
            Substitute.For<IDialogService>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            Substitute.For<Pia.Navigation.INavigationService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IVoiceInputService>(),
            columns,
            Substitute.For<ICollectionViewService>());
    }

    [Fact]
    public void ClosedColumnStaysExpandedAcrossAReload()
    {
        TodoViewModel? vm = null;

        WpfStaHost.Run(() =>
        {
            vm = Build();
            _ = vm.LoadTodosAsync();
            return 0;
        });
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            vm!.Columns.Single(c => c.IsClosedColumn).IsExpanded = true;
            _ = vm.LoadTodosAsync();
            return 0;
        });
        WpfStaHost.Pump();

        var expanded = WpfStaHost.Run(() => vm!.Columns.Single(c => c.IsClosedColumn).IsExpanded);
        Assert.True(expanded);
    }

    [Fact]
    public void ClosedColumnIsCollapsedOnTheFirstLoad()
    {
        TodoViewModel? vm = null;

        WpfStaHost.Run(() =>
        {
            vm = Build();
            _ = vm.LoadTodosAsync();
            return 0;
        });
        WpfStaHost.Pump();

        var expanded = WpfStaHost.Run(() => vm!.Columns.Single(c => c.IsClosedColumn).IsExpanded);
        Assert.False(expanded);
    }
}
