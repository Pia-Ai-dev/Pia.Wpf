using FluentAssertions;
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
        if (ShouldSkip) return;

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
        toolCalls.Should().Contain(tc => tc.ToolName == "create_todo",
            "LLM should call create_todo for a task request");

        var createCall = toolCalls.First(tc => tc.ToolName == "create_todo");
        var titleArg = createCall.Arguments?["title"]?.ToString() ?? "";
        titleArg.Should().ContainEquivalentOf("milk",
            "the todo title should mention milk");
    }

    [Fact]
    public async Task QueryTodos_ShouldCallQueryTodos()
    {
        if (ShouldSkip) return;

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
        toolCalls.Should().Contain(tc => tc.ToolName == "query_todos",
            "LLM should call query_todos when asked about tasks");
    }

    [Fact]
    public async Task CompleteTodo_ShouldUseCorrectId()
    {
        if (ShouldSkip) return;

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

        toolCalls.Should().Contain(tc => tc.ToolName == "query_todos",
            "LLM should query todos first to find the ID");

        toolCalls.Should().Contain(tc => tc.ToolName == "complete_todo",
            "LLM should call complete_todo to mark it done");

        if (queryIndex >= 0 && completeIndex >= 0)
        {
            queryIndex.Should().BeLessThan(completeIndex,
                "query should come before complete");
        }

        var completeCall = toolCalls.First(tc => tc.ToolName == "complete_todo");
        var idArg = completeCall.Arguments?["id"]?.ToString() ?? "";
        Guid.TryParse(idArg, out var parsedId).Should().BeTrue(
            "the ID argument should be a valid GUID");
        parsedId.Should().Be(knownGuid,
            "the complete call should target the existing todo");
    }
}
