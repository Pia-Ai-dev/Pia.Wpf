using FluentAssertions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Integration;

[Trait("Category", "Integration")]
public class MemoryToolIntegrationTests : ToolPipelineTestBase
{
    [Fact]
    public async Task RememberName_ShouldQueryThenCreate()
    {
        if (ShouldSkip) return;

        // Arrange: empty memory
        MemoryService.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>())
            .Returns(new List<MemoryObject>());
        MemoryService.GetMemorySummariesAsync(Arg.Any<string?>())
            .Returns(new List<MemorySummary>());
        MemoryService.CreateObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => new MemoryObject
            {
                Id = Guid.NewGuid(),
                Type = callInfo.ArgAt<string>(0),
                Label = callInfo.ArgAt<string>(1),
                Data = callInfo.ArgAt<string>(2),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "Remember that my name is John.", cts.Token);

        // Assert: should query/list first, then create
        var queryIndex = toolCalls.ToList().FindIndex(tc =>
            tc.ToolName == "query_memory" || tc.ToolName == "list_memories");
        var createIndex = toolCalls.ToList().FindIndex(tc =>
            tc.ToolName == "create_object");

        toolCalls.Should().Contain(tc =>
            tc.ToolName == "query_memory" || tc.ToolName == "list_memories",
            "LLM should check existing memories before creating");

        toolCalls.Should().Contain(tc =>
            tc.ToolName == "create_object",
            "LLM should create a memory object for the name");

        if (queryIndex >= 0 && createIndex >= 0)
        {
            queryIndex.Should().BeLessThan(createIndex,
                "query should come before create (memory workflow)");
        }

        var createCall = toolCalls.First(tc => tc.ToolName == "create_object");
        var dataArg = createCall.Arguments?["data"]?.ToString() ?? "";
        dataArg.Should().Contain("John", "the stored data should include the user's name");
    }

    [Fact]
    public async Task QueryExistingMemory_ShouldReturnData()
    {
        if (ShouldSkip) return;

        // Arrange: existing profile
        var knownGuid = Guid.NewGuid();
        var existingMemory = new MemoryObject
        {
            Id = knownGuid,
            Type = "personal_profile",
            Label = "Personal Profile",
            Data = """{"name": "John", "age": 30}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };

        MemoryService.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>())
            .Returns(new List<MemoryObject> { existingMemory });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "What is my name?", cts.Token);

        // Assert
        toolCalls.Should().Contain(tc => tc.ToolName == "query_memory",
            "LLM should query memory to look up the user's name");

        response.Should().Contain("John",
            "response should include the name from memory");
    }

    [Fact]
    public async Task UpdateExistingProfile_ShouldUseCorrectId()
    {
        if (ShouldSkip) return;

        // Arrange: existing profile with known GUID
        var knownGuid = Guid.NewGuid();
        var existingMemory = new MemoryObject
        {
            Id = knownGuid,
            Type = "personal_profile",
            Label = "Personal Profile",
            Data = """{"name": "John"}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };

        MemoryService.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>())
            .Returns(new List<MemoryObject> { existingMemory });
        MemoryService.GetObjectAsync(knownGuid)
            .Returns(existingMemory);
        MemoryService.GetMemorySummariesAsync(Arg.Any<string?>())
            .Returns(new List<MemorySummary>
            {
                new(knownGuid, "personal_profile", "Personal Profile")
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var (response, toolCalls) = await RunToolPipelineAsync(
            "My name changed to Jane.", cts.Token);

        // Assert: should query first, then update with correct ID
        toolCalls.Should().Contain(tc =>
            tc.ToolName == "query_memory" || tc.ToolName == "list_memories",
            "LLM should look up existing memory first");

        var updateCall = toolCalls.FirstOrDefault(tc => tc.ToolName == "update_object");
        if (updateCall is not null)
        {
            var idArg = updateCall.Arguments?["id"]?.ToString() ?? "";
            Guid.TryParse(idArg, out var parsedId).Should().BeTrue(
                "the ID argument should be a valid GUID");
            parsedId.Should().Be(knownGuid,
                "the update should target the existing memory object");
        }
        else
        {
            // LLM might choose create_object instead — that's acceptable behavior
            // as long as it queried first
            toolCalls.Should().Contain(tc => tc.ToolName == "create_object",
                "if no update_object, LLM should at least create a new object");
        }
    }
}
