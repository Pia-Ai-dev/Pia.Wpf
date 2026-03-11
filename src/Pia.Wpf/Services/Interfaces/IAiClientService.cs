using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IAiClientService
{
    Task<string> SendRequestAsync(AiProvider provider, string prompt, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        CancellationToken cancellationToken = default);

    Task<ChatResponse> GetChatResponseAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> GetChatCompletionWithToolsAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        Func<FunctionCallContent, Task<object?>>? toolHandler = null,
        CancellationToken cancellationToken = default);

    Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default);

    Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default);

    Task<string> OptimizeViaPiaCloudAsync(
        string text,
        Guid templateId,
        string language,
        bool isVoiceInput,
        CancellationToken cancellationToken = default);

    Task<string> GeneratePromptViaPiaCloudAsync(
        string styleDescription,
        CancellationToken cancellationToken = default);

    Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default);
}
