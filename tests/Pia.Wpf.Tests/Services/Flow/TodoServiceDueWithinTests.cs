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

public sealed class TodoServiceDueWithinTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _trackerDir;
    private readonly SqliteContext _context;
    private readonly TodoService _service;
    private readonly Guid _columnId = Guid.NewGuid();

    public TodoServiceDueWithinTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"flow-todo-{Guid.NewGuid():N}.db");
        _trackerDir = Path.Combine(Path.GetTempPath(), $"flow-todo-tracker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_trackerDir);
        _context = new SqliteContext(_dbPath);

        var columns = Substitute.For<IKanbanColumnService>();
        var tracker = new SyncDeleteTrackerService(_trackerDir, NullLogger<SyncDeleteTrackerService>.Instance);
        _service = new TodoService(_context, NullLogger<TodoService>.Instance, columns, tracker);
    }

    private Task Add(string title, DateTime? dueDate) =>
        _service.CreateAsync(title, TodoPriority.Medium, notes: null, dueDate: dueDate, columnId: _columnId);

    [Fact]
    public async Task GetDueWithinAsync_IncludesDueSoonOverdueAndUnspecifiedKind_ExcludesFarAndNull()
    {
        await Add("due-in-2h", DateTime.Now.AddHours(2));
        await Add("overdue", DateTime.Now.AddHours(-5));
        await Add("unspecified-midnight", DateTime.SpecifyKind(DateTime.Now.AddHours(3), DateTimeKind.Unspecified));
        await Add("due-in-48h", DateTime.Now.AddHours(48));
        await Add("no-due-date", null);

        var due = await _service.GetDueWithinAsync(TimeSpan.FromHours(24));

        var titles = due.Select(t => t.Title).ToHashSet();
        Assert.Contains("due-in-2h", titles);
        Assert.Contains("overdue", titles);
        Assert.Contains("unspecified-midnight", titles);
        Assert.DoesNotContain("due-in-48h", titles);
        Assert.DoesNotContain("no-due-date", titles);
    }

    public void Dispose()
    {
        _context.Dispose();
        TempPath.RemoveFile(_dbPath);
        TempPath.Remove(_trackerDir);
    }
}
