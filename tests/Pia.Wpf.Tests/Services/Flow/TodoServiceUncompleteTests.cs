using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Flow;

/// <summary>
/// Reopening a closed task must land it in the column marked as the default view, not in whichever
/// column happens to sort first.
/// </summary>
public sealed class TodoServiceUncompleteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _trackerDir;
    private readonly SqliteContext _context;
    private readonly TodoService _service;

    private readonly KanbanColumn _defaultView = new()
    {
        Id = Guid.NewGuid(),
        Name = "In progress",
        SortOrder = 5,
        IsDefaultView = true,
    };

    private readonly KanbanColumn _closed = new()
    {
        Id = Guid.NewGuid(),
        Name = "Closed",
        SortOrder = 99,
        IsClosedColumn = true,
    };

    private readonly Guid _otherColumnId = Guid.NewGuid();

    public TodoServiceUncompleteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"uncomplete-todo-{Guid.NewGuid():N}.db");
        _trackerDir = Path.Combine(Path.GetTempPath(), $"uncomplete-tracker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_trackerDir);
        _context = new SqliteContext(_dbPath);

        var columns = Substitute.For<IKanbanColumnService>();
        columns.GetDefaultViewColumnAsync().Returns(Task.FromResult(_defaultView));
        columns.GetClosedColumnAsync().Returns(Task.FromResult(_closed));

        var tracker = new SyncDeleteTrackerService(_trackerDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _service = new TodoService(_context, NullLogger<TodoService>.Instance, columns, tracker);
    }

    [Fact]
    public async Task UncompleteAsync_MovesTheTodoToTheDefaultViewColumn()
    {
        var todo = await _service.CreateAsync("write it down", columnId: _otherColumnId);
        await _service.CompleteAsync(todo.Id);

        await _service.UncompleteAsync(todo.Id);

        var reopened = await _service.GetAsync(todo.Id);
        Assert.NotNull(reopened);
        Assert.Equal(_defaultView.Id, reopened.ColumnId);
        Assert.Equal(TodoStatus.Pending, reopened.Status);
    }

    public void Dispose()
    {
        _context.Dispose();
        TempPath.RemoveFile(_dbPath);
        TempPath.Remove(_trackerDir);
    }
}
