using NSubstitute;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Integration;

[Trait("Category", "Integration")]
public class TodoToolIntegrationTests : ToolPipelineTestBase
{
    [Fact]
    public async Task CreateTodo_ShouldCallCreateTodo()
    {
        SkipIfNoApiKey();

        // Arrange
        TodoService.CreateAsync(Arg.Any<string>(), Arg.Any<TodoPriority>(),
                Arg.Any<string?>(), Arg.Any<DateTime?>())
            .Returns(callInfo => new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = callInfo.ArgAt<string>(0),
                Priority = callInfo.ArgAt<TodoPriority>(1),
                Status = TodoStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "Add a todo to buy milk.", cts.Token);

        // Assert
        Assert.Contains(toolCalls, tc => tc.ToolName == "create_todo");

        var createCall = toolCalls.First(tc => tc.ToolName == "create_todo");
        var titleArg = createCall.Arguments?["title"]?.ToString() ?? "";
        Assert.Contains("milk", titleArg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryTodos_ShouldCallQueryTodos()
    {
        SkipIfNoApiKey();

        // Arrange
        TodoService.GetPendingAsync().Returns(new List<TodoItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Buy milk",
                Priority = TodoPriority.Medium,
                Status = TodoStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        });
        TodoService.GetAllAsync().Returns(new List<TodoItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Buy milk",
                Priority = TodoPriority.Medium,
                Status = TodoStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "What are my todos?", cts.Token);

        // Assert
        Assert.Contains(toolCalls, tc => tc.ToolName == "query_todos");
    }

    [Fact]
    public async Task CompleteTodo_ShouldUseCorrectId()
    {
        SkipIfNoApiKey();

        // Arrange: existing todo
        var knownGuid = Guid.NewGuid();
        var existingTodo = new TodoItem
        {
            Id = knownGuid,
            Title = "Buy milk",
            Priority = TodoPriority.Medium,
            Status = TodoStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        TodoService.GetPendingAsync().Returns(new List<TodoItem> { existingTodo });
        TodoService.GetAllAsync().Returns(new List<TodoItem> { existingTodo });
        TodoService.GetAsync(knownGuid).Returns(existingTodo);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "Mark the milk todo as done.", cts.Token);

        // Assert: should query first, then complete with correct ID
        var queryIndex = toolCalls.ToList().FindIndex(tc => tc.ToolName == "query_todos");
        var completeIndex = toolCalls.ToList().FindIndex(tc => tc.ToolName == "complete_todo");

        Assert.Contains(toolCalls, tc => tc.ToolName == "query_todos");

        Assert.Contains(toolCalls, tc => tc.ToolName == "complete_todo");

        if (queryIndex >= 0 && completeIndex >= 0)
        {
            Assert.True(queryIndex < completeIndex);
        }

        var completeCall = toolCalls.First(tc => tc.ToolName == "complete_todo");
        var idArg = completeCall.Arguments?["id"]?.ToString() ?? "";
        Assert.True(Guid.TryParse(idArg, out var parsedId));
        Assert.Equal(knownGuid, parsedId);
    }
}
