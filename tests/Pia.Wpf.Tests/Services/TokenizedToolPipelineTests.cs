using System.IO;
using System.Text;
using System.Text.Json;
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

    private const string Masked = "[Person_1]";

    private readonly string _dir;
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly TokenMapService _tokenMap;
    private readonly FilesToolHandler _files;

    public TokenizedToolPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTokenPipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var appSettings = new AppSettings { AssistantFilesFolder = _dir };
        appSettings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Schmidt" });
        _settings.GetSettingsAsync().Returns(appSettings);

        var memory = Substitute.For<IMemoryService>();
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        memory.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile).Returns(new List<MemoryObject>
        {
            new()
            {
                Type = MemoryObjectTypes.PersonalProfile,
                Label = "Profile",
                Data = """{"name": "Max", "nickname": "Jürgen"}""",
            },
        });

        _tokenMap = new TokenMapService(new StructuredPiiDetector(), memory, _settings);
        _files = new FilesToolHandler(_settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
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

    /// <summary>Tokenizing rewrites text and detokenizes arguments, never an id: the server matches on the toolu_ id.</summary>
    [Fact]
    public async Task Tokenizing_KeepsEveryCallIdVerbatim_InTheRequestAndAtTheHandler()
    {
        var detector = Substitute.For<IPiiDetector>();
        detector.DetectPii(Arg.Any<string>()).Returns((IReadOnlyList<PiiMatch>)[]);
        detector.DetectPiiInStructured(Arg.Any<string>(), Arg.Any<string>()).Returns((IReadOnlyList<PiiMatch>)[]);
        var memory = Substitute.For<IMemoryService>();
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var tokenMap = new TokenMapService(detector, memory, settings);

        IList<ChatMessage>? request = null;
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                request = ci.ArgAt<IList<ChatMessage>>(0);
                return OneCall(ci.ArgAt<ToolCallHandler?>(3), "toolu_02Qrs");
            });
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());
        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        List<ChatMessage> history =
        [
            new(ChatRole.User, "summarise"),
            new(ChatRole.Assistant,
            [
                new TextContent("reading it"),
                new FunctionCallContent("toolu_01AbC-dEf_9", "read_file", new Dictionary<string, object?> { ["path"] = "a.csv" }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_01AbC-dEf_9", "a body")]),
        ];
        var handled = new List<string>();
        ToolCallHandler handler = (call, _) =>
        {
            handled.Add(call.CallId);
            return Task.FromResult<object?>("ok");
        };

        TokenMapAmbient.Current = tokenMap;
        try
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                history,
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null, toolHandler: handler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        Assert.NotNull(request);
        Assert.NotSame(history, request);
        Assert.Equal("toolu_01AbC-dEf_9",
            Assert.Single(request!.SelectMany(m => m.Contents).OfType<FunctionCallContent>()).CallId);
        Assert.Equal("toolu_01AbC-dEf_9",
            Assert.Single(request!.SelectMany(m => m.Contents).OfType<FunctionResultContent>()).CallId);
        Assert.Equal(["toolu_02Qrs"], handled);
    }

    [Fact]
    public async Task ACodeFileThatSpellsAKnownName_IsWrittenBackByteForByte()
    {
        const string source =
            "// Max wrote this; see maria-schmidt.md\n" +
            "var m = max(a, b);\n" +
            "var maximum = Max(m, MaxLength);\n" +
            "return Max_Mustermann(maximum);";
        const string added = "\n// reviewed";
        var path = WriteFixture("Calc.cs", source);

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "review Calc.cs")],
            FileToolHandler,
            _ => Call("read_file", ("path", "Calc.cs")),
            seen => Call("write_file", ("path", "Calc.cs"), ("content", CopiedContent(seen[0]) + added)));

        var view = Assert.IsType<string>(results[0]);
        Assert.Contains("var m = max(a, b);", view, StringComparison.Ordinal);
        Assert.Contains($"var maximum = {Masked}(m, MaxLength);", view, StringComparison.Ordinal);
        Assert.Contains($"return {Masked}_Mustermann(maximum);", view, StringComparison.Ordinal);
        Assert.Contains($"// {Masked} wrote this; see maria-schmidt.md", view, StringComparison.Ordinal);

        AssertSucceeded(results[1]);
        Assert.Equal(Encoding.UTF8.GetBytes(source + added), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AnEditAnchorCopiedFromTheReadResult_MatchesTheFile()
    {
        const string source = "var m = max(a, b);\nvar maximum = Max(m, max(a, b));\nreturn maximum;";
        var path = WriteFixture("Calc.cs", source);

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "rename the variable")],
            FileToolHandler,
            _ => Call("read_file", ("path", "Calc.cs")),
            seen =>
            {
                var anchor = ViewLine(seen[0], 2);
                return Call("edit_file",
                    ("path", "Calc.cs"), ("old_string", anchor), ("new_string", anchor.Replace("maximum", "largest")));
            });

        Assert.Equal($"2|var maximum = {Masked}(m, max(a, b));", ((string)results[0]!).Split('\n')[2]);
        AssertSucceeded(results[1]);
        Assert.Equal("var m = max(a, b);\nvar largest = Max(m, max(a, b));\nreturn maximum;", File.ReadAllText(path));
    }

    [Fact]
    public async Task ANearMissOfACustomKeyword_KeepsItsSpelling()
    {
        const string source = "Scanned: Schmitt.pdf\nOwner: Schmidt";
        var path = WriteFixture("Inbox.md", source);

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "tidy the inbox note")],
            FileToolHandler,
            _ => Call("read_file", ("path", "Inbox.md")),
            seen => Call("write_file", ("path", "Inbox.md"), ("content", CopiedContent(seen[0]) + "\nDone.")));

        var view = Assert.IsType<string>(results[0]);
        Assert.Contains("1|Scanned: Schmitt.pdf", view, StringComparison.Ordinal);
        Assert.Contains("2|Owner: [Custom_1]", view, StringComparison.Ordinal);

        AssertSucceeded(results[1]);
        Assert.Equal(source + "\nDone.", File.ReadAllText(path));
    }

    [Fact]
    public async Task AnExactWholeWordName_AndNewContactDetails_AreStillMasked()
    {
        const string source = "Owner: Max\nMail: anna.berg@example.org\nPhone: 030 1234567";
        var path = WriteFixture("Contact.txt", source);

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "add a note")],
            FileToolHandler,
            _ => Call("read_file", ("path", "Contact.txt")),
            seen => Call("write_file", ("path", "Contact.txt"), ("content", CopiedContent(seen[0]) + "\nChecked.")));

        var view = Assert.IsType<string>(results[0]);
        Assert.Equal($"total_lines=3\n1|Owner: {Masked}\n2|Mail: [Email_1]\n3|Phone: [Phone_1]", view);

        AssertSucceeded(results[1]);
        Assert.Equal(source + "\nChecked.", File.ReadAllText(path));
    }

    [Fact]
    public async Task AnObjectResultWithALineBreakBeforeAName_IsMasked()
    {
        var hit = new Dictionary<string, string>
        {
            ["body"] = "Hallo\nMax",
            ["signature"] = "Grüße, Jürgen",
            ["phone"] = "+49 30 1234567",
        };

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "what did the mail say?")],
            (_, _) => Task.FromResult<object?>(new { hits = new[] { hit } }),
            _ => Call("recall", ("query", "mail")));

        var json = Assert.IsType<string>(results[0]);
        Assert.Contains($"\"body\":\"Hallo\\n{Masked}\"", json, StringComparison.Ordinal);
        Assert.Contains("[Person_2]", json, StringComparison.Ordinal);
        Assert.Contains("\"phone\":\"[Phone_1]\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Max", json, StringComparison.Ordinal);
        Assert.DoesNotContain("rgen", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567", json, StringComparison.Ordinal);

        // Every leaf detokenizes to exactly what the tool returned.
        using var restored = JsonDocument.Parse(_tokenMap.Detokenize(json));
        var restoredHit = restored.RootElement.GetProperty("hits")[0];
        foreach (var (name, value) in hit)
            Assert.Equal(value, restoredHit.GetProperty(name).GetString());
    }

    [Fact]
    public async Task AnObjectResultWithoutPii_IsTheSerializersOwnJson()
    {
        var result = new { count = 3, ok = true, ratio = 1.50m, items = new[] { "a\nb", "Grüße + 50%" }, none = (string?)null };

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "list them")],
            (_, _) => Task.FromResult<object?>(result),
            _ => Call("query_items", ("filter", "all")));

        Assert.Equal(JsonSerializer.Serialize(result), Assert.IsType<string>(results[0]));
    }

    [Fact]
    public async Task ANumberInAnObjectResult_ThatIsAStoredValue_IsMaskedAsAString()
    {
        _tokenMap.Tokenize("123456", "Custom");

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "look up the customer")],
            (_, _) => Task.FromResult<object?>(new { customerNo = 123456, orderNo = 1234567, count = 3 }),
            _ => Call("query_customers", ("filter", "all")));

        var json = Assert.IsType<string>(results[0]);
        Assert.Equal("""{"customerNo":"[Custom_1]","orderNo":1234567,"count":3}""", json);
        using var restored = JsonDocument.Parse(_tokenMap.Detokenize(json));
        Assert.Equal("123456", restored.RootElement.GetProperty("customerNo").GetString());
    }

    [Fact]
    public async Task AnObjectResultWithManyNewContactDetails_DoesNotRebuildTheMatcherPerLeaf()
    {
        var hits = Enumerable.Range(0, 200)
            .Select(i => new Dictionary<string, string>
            {
                ["from"] = $"sender{i}@example.org",
                ["body"] = $"call 030 {1000000 + i}",
            })
            .ToArray();
        var buildsBefore = _tokenMap.VerbatimMatcherBuilds;

        var (_, results) = await RunAsync(
            [new(ChatRole.User, "what came in?")],
            (_, _) => Task.FromResult<object?>(new { hits }),
            _ => Call("recall", ("query", "mail")));

        // One build for the result, one more once the 400 spellings it adds pass the cap on unmatched ones.
        Assert.InRange(_tokenMap.VerbatimMatcherBuilds - buildsBefore, 0, 2);
        var json = Assert.IsType<string>(results[0]);
        Assert.DoesNotContain("@example.org", json, StringComparison.Ordinal);
        Assert.DoesNotContain("030 1", json, StringComparison.Ordinal);
        using var restored = JsonDocument.Parse(_tokenMap.Detokenize(json));
        var restoredHits = restored.RootElement.GetProperty("hits");
        for (var i = 0; i < hits.Length; i++)
        {
            foreach (var (name, value) in hits[i])
                Assert.Equal(value, restoredHits[i].GetProperty(name).GetString());
        }
    }

    [Fact]
    public async Task TheUsersOwnMessage_KeepsCaseInsensitiveAndFuzzyMatching()
    {
        var (sent, _) = await RunAsync(
            [new(ChatRole.User, "fix max() in Schmitt.pdf for SCHMIDT")],
            FileToolHandler);

        Assert.Equal($"fix {Masked}() in [Custom_1].pdf for [Custom_1]", sent[0].Text);
    }

    [Fact]
    public async Task AReplayedAssistantReply_IsTokenizedVerbatim()
    {
        var (sent, _) = await RunAsync(
            [
                new(ChatRole.User, "what changed?"),
                new(ChatRole.Assistant, "Max renamed max() and maximum; Schmitt.pdf stays, Schmidt.pdf too."),
            ],
            FileToolHandler);

        Assert.Equal(ChatRole.Assistant, sent[1].Role);
        Assert.Equal($"{Masked} renamed max() and maximum; Schmitt.pdf stays, [Custom_1].pdf too.", sent[1].Text);
    }

    /// <summary>Runs the real decorator around a scripted model; each step builds its call from the results the provider saw.</summary>
    private async Task<(IList<ChatMessage> Sent, List<object?> Results)> RunAsync(
        IList<ChatMessage> messages,
        ToolCallHandler toolHandler,
        params Func<IReadOnlyList<object?>, FunctionCallContent>[] modelCalls)
    {
        IList<ChatMessage>? sent = null;
        var results = new List<object?>();

        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent = ci.ArgAt<IList<ChatMessage>>(0);
                return Model(ci.ArgAt<ToolCallHandler?>(3)!);
            });

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());
        var sut = new TokenizingAiClientService(
            inner, serviceProvider, _settings, NullLogger<TokenizingAiClientService>.Instance);

        TokenMapAmbient.Current = _tokenMap;
        try
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                messages,
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null, toolHandler: toolHandler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        Assert.NotNull(sent);
        return (sent!, results);

        async IAsyncEnumerable<ChatStreamItem> Model(ToolCallHandler wrapped)
        {
            var round = 1;
            foreach (var next in modelCalls)
                results.Add(await wrapped(next(results), new ToolDispatchContext(round++)));
            yield return new Finished(null, "gpt-5");
        }
    }

    /// <summary>Approves every write, as the user would.</summary>
    private async Task<object?> FileToolHandler(FunctionCallContent call, ToolDispatchContext _)
    {
        var (result, pending) = await _files.HandleToolCallAsync(call);
        return pending is null ? result : await pending.Execute();
    }

    private string WriteFixture(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static FunctionCallContent Call(string tool, params (string Name, string Value)[] arguments) =>
        new("call-" + tool, tool, arguments.ToDictionary(a => a.Name, a => (object?)a.Value));

    /// <summary>What a model copies out of read_file: the lines without the header and the <c>N|</c> prefixes.</summary>
    private static string CopiedContent(object? readResult) =>
        string.Join("\n", ((string)readResult!).Split('\n').Skip(1).Select(StripLineNumber));

    private static string ViewLine(object? readResult, int lineNumber) =>
        StripLineNumber(((string)readResult!).Split('\n')[lineNumber]);

    private static string StripLineNumber(string line) => line[(line.IndexOf('|') + 1)..];

    private static void AssertSucceeded(object? writeResult)
    {
        var json = Assert.IsType<string>(writeResult);
        Assert.Contains("\"success\":true", json, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<ChatStreamItem> OneCall(ToolCallHandler? handler, string callId)
    {
        await Task.Yield();
        if (handler is not null)
            await handler(new FunctionCallContent(callId, "read_file", new Dictionary<string, object?>()), new ToolDispatchContext(1));

        yield return new TextDelta("done");
        yield return new Finished(null, "test-model");
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
