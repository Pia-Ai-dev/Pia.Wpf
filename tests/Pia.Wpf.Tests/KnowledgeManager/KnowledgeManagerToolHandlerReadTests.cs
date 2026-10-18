using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public class KnowledgeManagerToolHandlerTestBase : IDisposable
{
    protected static readonly Guid Kb = Guid.Parse("11111111-1111-1111-1111-111111111111");
    protected static readonly Guid Doc = Guid.Parse("22222222-2222-2222-2222-222222222222");

    protected readonly string Root = Path.Combine(Path.GetTempPath(), "pia-kbm-h-" + Guid.NewGuid().ToString("N"));
    protected readonly IKnowledgeManagerApiClient Api = Substitute.For<IKnowledgeManagerApiClient>();
    protected readonly IKnowledgeManagerSurfaceCache Surface = Substitute.For<IKnowledgeManagerSurfaceCache>();
    protected readonly IFilesToolHandler Files = Substitute.For<IFilesToolHandler>();
    protected readonly ILocalizationService Localization = Substitute.For<ILocalizationService>();

    protected KnowledgeManagerToolHandlerTestBase()
    {
        Directory.CreateDirectory(Root);
        Surface.IsAvailable.Returns(true);
        Files.IsAvailable.Returns(true);
        Files.ResolveToolRoot().Returns(Root);
        Localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        Localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join("|", ci.ArgAt<object[]>(1))})");
    }

    public void Dispose() => TempPath.Remove(Root);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected KnowledgeManagerToolHandler CreateSut() =>
        new(Api, Surface, Files, Localization, NullLogger<KnowledgeManagerToolHandler>.Instance);

    protected static FunctionCallContent Call(string name, params (string Key, object? Value)[] args) =>
        new("call-1", name, args.ToDictionary(a => a.Key, a => a.Value));

    protected static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    protected void KnowledgeBases(params KbManagerKnowledgeBase[] rows) =>
        Api.ListKnowledgeBasesAsync(Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>(KbManagerCallStatus.Ok, rows));

    protected void Documents(params KbManagerDocument[] rows) =>
        Api.ListDocumentsAsync(Kb, Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<IReadOnlyList<KbManagerDocument>>(KbManagerCallStatus.Ok, rows));

    protected static KbManagerKnowledgeBase Handbook(bool shared = false) => new(Kb, "Handbook", 1, shared, true);

    protected static KbManagerDocument Onboarding(long size = 12) => new(
        Doc, "Onboarding", null, KbManagerLimits.Markdown, "Ready", size, null, 3, 90, 5,
        null, DateTime.UtcNow, DateTime.UtcNow);
}

public sealed class KnowledgeManagerToolHandlerReadTests : KnowledgeManagerToolHandlerTestBase
{
    [Fact]
    public void WhenHidden_ItOffersNoTools()
    {
        Surface.IsAvailable.Returns(false);

        Assert.Empty(CreateSut().GetTools());
    }

    [Fact]
    public void WhenAvailable_ItOffersAllTenTools()
    {
        var names = CreateSut().GetTools().Select(t => t.Name).ToList();

        Assert.Equal(
            ["list_knowledge_bases", "get_knowledge_base_stats", "list_kb_documents", "get_kb_prompt",
             "read_kb_document", "download_kb_document", "upload_kb_document", "update_kb_document",
             "set_kb_prompt", "delete_kb_document"],
            names);
    }

    [Fact]
    public async Task ListKnowledgeBases_AnswersInlineWithTheSharedFlag()
    {
        KnowledgeBases(Handbook(shared: true));

        var (result, pending) = await CreateSut().HandleToolCallAsync(Call("list_knowledge_bases"), Ct);

        Assert.Null(pending);
        var kb = Json(result).GetProperty("knowledge_bases")[0];
        Assert.Equal(Kb, kb.GetProperty("kb_id").GetGuid());
        Assert.True(kb.GetProperty("shared").GetBoolean());
    }

    [Fact]
    public async Task ListKnowledgeBases_WithNone_SaysSo()
    {
        KnowledgeBases();

        var (result, _) = await CreateSut().HandleToolCallAsync(Call("list_knowledge_bases"), Ct);

        Assert.Equal("Your group has no knowledge bases you can manage.", result);
    }

    [Fact]
    public async Task Stats_AnswersInline()
    {
        Api.GetStatsAsync(Kb, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerStats>(
            KbManagerCallStatus.Ok, new KbManagerStats(2, 1, 0, 1, 0, 100, 1000, 10, 4, 80, 7, null, 55)));

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("get_knowledge_base_stats", ("kb_id", Kb.ToString())), Ct);

        Assert.Null(pending);
        Assert.Equal(55, Json(result).GetProperty("embedding_tokens_this_month").GetInt64());
    }

    [Fact]
    public async Task AnUnparseableKbId_IsRefusedWithoutACall()
    {
        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("list_kb_documents", ("kb_id", "not-a-guid")), Ct);

        Assert.Equal("kb_id must be an id from list_knowledge_bases.", result);
        await Api.DidNotReceiveWithAnyArgs().ListDocumentsAsync(default, Ct);
    }

    [Fact]
    public async Task ReadDocument_UnderTheLimit_ReturnsTheWholeText()
    {
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
            KbManagerCallStatus.Ok, new KbManagerDocumentContent("short", KbManagerLimits.PlainText)));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("read_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        var json = Json(result);
        Assert.Equal("short", json.GetProperty("content").GetString());
        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ReadDocument_OverTheLimit_IsTruncatedWithAPointerToDownload()
    {
        var text = new string('a', KbManagerLimits.MaxInlineContentBytes + 10);
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
            KbManagerCallStatus.Ok, new KbManagerDocumentContent(text, KbManagerLimits.PlainText)));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("read_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        var json = Json(result);
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(KbManagerLimits.MaxInlineContentBytes, json.GetProperty("content").GetString()!.Length);
        Assert.Contains("download_kb_document", json.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateUtf8_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 7) + "😀" + "bbb";

        var (cut, truncated) = KnowledgeManagerToolHandler.TruncateUtf8(text, 9);

        Assert.True(truncated);
        Assert.Equal(new string('a', 7), cut);
    }

    [Fact]
    public void TruncateUtf8_NeverSplitsAMultibyteCharacter()
    {
        var (cut, truncated) = KnowledgeManagerToolHandler.TruncateUtf8("aäb", 2);

        Assert.True(truncated);
        Assert.Equal("a", cut);
        Assert.Equal(1, Encoding.UTF8.GetByteCount(cut));
    }

    [Fact]
    public async Task Download_SavesANewFileAndNeverOverwrites()
    {
        Documents(Onboarding());
        File.WriteAllText(Path.Combine(Root, "Onboarding.md"), "mine");
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
            KbManagerCallStatus.Ok, new KbManagerDocumentContent("theirs", KbManagerLimits.Markdown)));

        var (result, pending) = await CreateSut().HandleToolCallAsync(
            Call("download_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Null(pending);
        Assert.Equal("Onboarding (1).md", Json(result).GetProperty("path").GetString());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Root, "Onboarding.md")));
    }

    [Fact]
    public async Task Download_WithNoFilesFolder_SaysSo()
    {
        Files.ResolveToolRoot().Returns((string?)null);

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("download_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.StartsWith("No assistant files folder is configured", (string)result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnowledgeDisabledOnTheServer_SaysSoInsteadOfAskingForARetry()
    {
        Api.GetStatsAsync(Kb, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerStats>(
            KbManagerCallStatus.Unavailable, null,
            new KbManagerError(KbManagerErrorCodes.KnowledgeDisabled, "Knowledge bases are disabled.", null, null, null, null)));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("get_knowledge_base_stats", ("kb_id", Kb.ToString())), Ct);

        Assert.Equal(
            "Knowledge bases are switched off on your Pia server, so nothing was read or changed. "
            + "Only a server administrator can switch them on.",
            result);
    }

    [Fact]
    public async Task AFeatureNotLicensedAnswer_NamesTheLicenseAndHidesTheSurface()
    {
        Api.GetStatsAsync(Kb, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerStats>(
            KbManagerCallStatus.Forbidden, null,
            new KbManagerError("feature_not_licensed", null, null, null, null, null)));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("get_knowledge_base_stats", ("kb_id", Kb.ToString())), Ct);

        Surface.Received(1).Hide();
        Assert.Equal(
            "Your Pia server's license no longer includes knowledge bases, so nothing was read or changed.", result);
    }

    [Fact]
    public async Task AnUnavailableAnswerWithoutAKnownCode_StillAsksForARetry()
    {
        Api.GetStatsAsync(Kb, Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerStats>(KbManagerCallStatus.Unavailable));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("get_knowledge_base_stats", ("kb_id", Kb.ToString())), Ct);

        Assert.Equal("Your Pia server could not answer, so nothing was read or changed — try again.", result);
    }

    [Fact]
    public async Task AnyForbiddenAnswer_HidesTheSurface()
    {
        Api.GetStatsAsync(Kb, Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerStats>(KbManagerCallStatus.Forbidden));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("get_knowledge_base_stats", ("kb_id", Kb.ToString())), Ct);

        Surface.Received(1).Hide();
        Assert.Equal("The server no longer lets you manage knowledge bases, so nothing was read or changed.", result);
    }

    [Fact]
    public async Task ADocumentThatIsNotThere_IsNamedAsSuch()
    {
        KnowledgeBases(Handbook());
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerDocumentContent>(KbManagerCallStatus.NotFound));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("read_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Equal("That document is not in this knowledge base. Call list_kb_documents for the ids.", result);
    }

    [Fact]
    public async Task AKnowledgeBaseThatIsNotThere_IsNamedAsSuch_NotAsAMissingDocument()
    {
        KnowledgeBases();
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerDocumentContent>(KbManagerCallStatus.NotFound));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("read_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Assert.Equal("That knowledge base is not one you can manage. Call list_knowledge_bases for the ids.", result);
    }

    [Fact]
    public async Task ANotFound_WhenTheKnowledgeBaseLookupIsRefused_ReportsThatRefusal()
    {
        Api.ListKnowledgeBasesAsync(Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>(KbManagerCallStatus.Forbidden));
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerDocumentContent>(KbManagerCallStatus.NotFound));

        var (result, _) = await CreateSut().HandleToolCallAsync(
            Call("read_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

        Surface.Received(1).Hide();
        Assert.Equal("The server no longer lets you manage knowledge bases, so nothing was read or changed.", result);
    }
}
