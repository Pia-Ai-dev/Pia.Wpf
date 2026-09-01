using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The whole round trip, with a REAL token map: what a tool result shows the model, what the model copies into
/// the next call, and what therefore lands on disk. BG1's overspend-report.md shipped "[Phone_9]" where a date
/// belonged, and no unit test could see it because the defect lives in the seam between three components.
/// </summary>
[Collection("TokenizationLatchStatic")]
public sealed class TokenizedToolPipelineTests : IDisposable
{
    // The known hyphenated-date defect, reproduced deliberately rather than fixed here: this batch routes
    // around it, and a fake detector keeps the reproduction exact instead of depending on the real patterns.
    private const string TheDate = "2026-03-27";
    private const string FileBody = "2026-03-27,travel,1500.00,client visit PENDING";

    private readonly string _dir;

    public TokenizedToolPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTokenPipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TempPath.Remove(_dir);
    }

    [Fact]
    public async Task AToolResultsPlaceholder_IsRestoredBeforeTheNextToolWritesIt()
    {
        var detector = Substitute.For<IPiiDetector>();
        detector.DetectPii(Arg.Any<string>()).Returns(ci =>
        {
            var text = (string)ci[0];
            var at = text.IndexOf(TheDate, StringComparison.Ordinal);
            return at < 0
                ? (IReadOnlyList<PiiMatch>)[]
                : [new PiiMatch(TheDate, "Phone", at, TheDate.Length)];
        });
        detector.DetectPiiInStructured(Arg.Any<string>(), Arg.Any<string>()).Returns((IReadOnlyList<PiiMatch>)[]);

        var memory = Substitute.For<IMemoryService>();
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var tokenMap = new TokenMapService(detector, memory, settings);

        // The "model": it reads a file, then writes back exactly the bytes the result showed it. That copy is
        // the whole defect — nothing here decides to keep a placeholder, it just echoes what it was given.
        string? resultTheModelSaw = null;
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => EchoRounds(ci.ArgAt<ToolCallHandler?>(3), r => resultTheModelSaw = r));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());
        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        var written = Path.Combine(_dir, "overspend-report.md");
        ToolCallHandler handler = (call, _) =>
        {
            if (call.Name == "read_file")
                return Task.FromResult<object?>(FileBody);
            File.WriteAllText(written, (string)call.Arguments!["content"]!);
            return Task.FromResult<object?>("written");
        };

        TokenMapAmbient.Current = tokenMap;
        try
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "summarise the expenses") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null, toolHandler: handler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        // The premise: the model really did see a placeholder, so this is not passing because nothing was
        // tokenized at all.
        Assert.NotNull(resultTheModelSaw);
        Assert.DoesNotContain(TheDate, resultTheModelSaw!, StringComparison.Ordinal);
        Assert.Contains("[Phone_", resultTheModelSaw!, StringComparison.Ordinal);

        // The deliverable: what a person opens has the real value, not the token.
        var onDisk = await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken);
        Assert.Contains(TheDate, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("[Phone_", onDisk, StringComparison.Ordinal);
    }

    /// <summary>Round 1 reads, round 2 writes back what round 1 returned — the two-round shape the tool loop runs.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> EchoRounds(ToolCallHandler? handler, Action<string> observed)
    {
        await Task.Yield();
        if (handler is not null)
        {
            var read = await handler(
                new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "expenses.csv" }),
                new ToolDispatchContext(1));
            var seen = read as string ?? string.Empty;
            observed(seen);

            await handler(
                new FunctionCallContent("c2", "write_file", new Dictionary<string, object?>
                {
                    ["path"] = "overspend-report.md",
                    ["content"] = seen,
                }),
                new ToolDispatchContext(2));
        }

        yield return new TextDelta("done");
        yield return new Finished(null, "test-model");
    }
}
