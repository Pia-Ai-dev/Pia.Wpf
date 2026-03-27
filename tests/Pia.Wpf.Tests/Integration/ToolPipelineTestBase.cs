using System.Net.Http;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.Tests.Integration;

public abstract class ToolPipelineTestBase
{
    protected static string? ApiKey =>
        Environment.GetEnvironmentVariable("PIA_TEST_API_KEY");

    protected bool ShouldSkip => string.IsNullOrEmpty(ApiKey);

    protected readonly IMemoryService MemoryService = Substitute.For<IMemoryService>();
    protected readonly ITodoService TodoService = Substitute.For<ITodoService>();
    protected readonly IReminderService ReminderService = Substitute.For<IReminderService>();
    protected readonly IEmbeddingService EmbeddingService = Substitute.For<IEmbeddingService>();

    private readonly MemoryToolHandler _memoryToolHandler;
    private readonly TodoToolHandler _todoToolHandler;
    private readonly ReminderToolHandler _reminderToolHandler;
    private readonly AiClientService _aiClientService;

    // NOTE: Keep in sync with AssistantViewModel.BuildSystemPrompt (line 20).
    // Copied here to avoid making the private method internal just for tests.
    private static string BuildSystemPrompt() => $"""
        You are Pia, a helpful personal assistant. Provide concise, accurate, and friendly responses.
        The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

        You have a persistent memory system. When the user asks about something personal or tells you
        something to remember, use your memory tools to look it up or store it. Use list_memories to see
        what's stored, and query_memory to retrieve details.

        You have access to a todo list for managing the user's tasks.

        Tools: create_todo, query_todos, complete_todo, update_todo, delete_todo.

        When a user mentions something they need to do, offer to add it as a todo.
        When creating or updating a todo with a due date, suggest setting a reminder
        so they don't forget. Use the create_reminder tool if they agree.
        When listing todos, highlight any that are overdue (past due date, still pending).

        TOOL SELECTION — follow this decision tree strictly:
        1. Does the request mention a specific TIME, DATE, or SCHEDULE for notification?
           YES → Use Reminder tools. NOT a reminder: "Remember I like coffee" (no time = memory).
           NO → Continue to step 2.
        2. Does the request involve a TASK, ACTION ITEM, or something to DO?
           YES → Use Todo tools. NOT a todo: "Remember my WiFi password" (information = memory).
           NO → Continue to step 3.
        3. Does the request involve STORING, RECALLING, or UPDATING personal information?
           YES → Use Memory tools (remember: query first, then create/update).
           NOT a memory: "Remind me at 3 PM to call Bob" (has time = reminder).
           NO → Respond conversationally without tools.

        Key principles:
        - Memory workflow — ALWAYS follow this sequence when storing information:
          1. First call query_memory to check if a related memory already exists.
          2. If a match is found, use update_object to modify it (do NOT create a duplicate).
          3. Only if no related memory exists, use create_object to store it as new.
          This applies whenever the user shares a fact, preference, or personal detail — even if
          they say "remember" or "create". The intent is to keep memory up to date,
          not to accumulate duplicates.
        - When the user asks about their reminders, use query_reminders. To modify or cancel, first
          query to find the ID.
        - When a user declines a proposed action, do NOT retry the same operation. Instead, acknowledge
          the decline and ask the user what they would like to do differently or if they want to adjust
          the details.
        """;

    private static readonly string[] MemoryToolNames =
        ["create_object", "update_object", "append_to_list", "delete_object", "list_memories", "query_memory"];

    private static readonly string[] TodoToolNames =
        ["create_todo", "query_todos", "complete_todo", "update_todo", "delete_todo"];

    private static readonly string[] ReminderToolNames =
        ["create_reminder", "query_reminders", "update_reminder", "delete_reminder"];

    protected ToolPipelineTestBase()
    {
        EmbeddingService.IsModelAvailable.Returns(false);

        _memoryToolHandler = new MemoryToolHandler(
            MemoryService,
            EmbeddingService,
            NullLogger<MemoryToolHandler>.Instance);

        _todoToolHandler = new TodoToolHandler(
            TodoService,
            NullLogger<TodoToolHandler>.Instance);

        _reminderToolHandler = new ReminderToolHandler(
            ReminderService,
            NullLogger<ReminderToolHandler>.Instance);

        var dpapiHelper = Substitute.ForPartsOf<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        dpapiHelper.Decrypt(Arg.Any<string>()).Returns(ApiKey ?? string.Empty);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());

        _aiClientService = new AiClientService(
            dpapiHelper,
            httpClientFactory,
            settingsService,
            NullLogger<AiClientService>.Instance);
    }

    protected AiProvider CreateTestProvider()
    {
        var endpoint = Environment.GetEnvironmentVariable("PIA_TEST_ENDPOINT")
            ?? "https://api.openai.com/v1";
        var model = Environment.GetEnvironmentVariable("PIA_TEST_MODEL")
            ?? "gpt-4o-mini";

        return new AiProvider
        {
            Name = "Integration Test Provider",
            ProviderType = AiProviderType.OpenAI,
            Endpoint = endpoint,
            ModelName = model,
            EncryptedApiKey = "test-key-placeholder",
            SupportsToolCalling = true,
            SupportsStreaming = false,
            TimeoutSeconds = 60
        };
    }

    protected async Task<(string Response, IReadOnlyList<ToolCallRecord> ToolCalls)> RunToolPipelineAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var provider = CreateTestProvider();
        var tools = new List<AITool>();
        tools.AddRange(_memoryToolHandler.GetTools());
        tools.AddRange(_todoToolHandler.GetTools());
        tools.AddRange(_reminderToolHandler.GetTools());

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt()),
            new(ChatRole.User, userMessage)
        };

        var toolCalls = new List<ToolCallRecord>();
        var responseBuilder = new StringBuilder();

        await foreach (var token in _aiClientService.GetChatCompletionWithToolsAsync(
            messages, provider, tools,
            async toolCall => await HandleToolCallAsync(toolCall, toolCalls),
            cancellationToken))
        {
            responseBuilder.Append(token);
        }

        return (responseBuilder.ToString(), toolCalls);
    }

    private async Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        List<ToolCallRecord> toolCalls)
    {
        object? result;

        if (MemoryToolNames.Contains(toolCall.Name))
        {
            var (immediateResult, pendingAction) = await _memoryToolHandler.HandleToolCallAsync(toolCall);
            if (immediateResult is not null)
            {
                result = immediateResult;
            }
            else if (pendingAction is not null)
            {
                result = await _memoryToolHandler.ExecutePendingActionAsync(pendingAction);
            }
            else
            {
                result = "Tool call handled.";
            }
        }
        else if (TodoToolNames.Contains(toolCall.Name))
        {
            var (immediateResult, pendingAction) = await _todoToolHandler.HandleToolCallAsync(toolCall);
            if (immediateResult is not null)
            {
                result = immediateResult;
            }
            else if (pendingAction is not null)
            {
                result = await _todoToolHandler.ExecutePendingActionAsync(pendingAction);
            }
            else
            {
                result = "Tool call handled.";
            }
        }
        else if (ReminderToolNames.Contains(toolCall.Name))
        {
            var (immediateResult, pendingAction) = await _reminderToolHandler.HandleToolCallAsync(toolCall);
            if (immediateResult is not null)
            {
                result = immediateResult;
            }
            else if (pendingAction is not null)
            {
                result = await _reminderToolHandler.ExecutePendingActionAsync(pendingAction);
            }
            else
            {
                result = "Tool call handled.";
            }
        }
        else
        {
            result = $"Unknown tool: {toolCall.Name}";
        }

        toolCalls.Add(new ToolCallRecord(toolCall.Name, toolCall.Arguments, result));
        return result;
    }
}
